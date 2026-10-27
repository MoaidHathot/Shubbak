using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Shubbak.Native;

namespace Shubbak.InputProbe;

/// <summary>A bounded, local-only WebGL workload in a disposable browser profile.</summary>
internal static class GpuLoad
{
    internal static async Task<int> Run(string[] args)
    {
        int seconds = Program.Number(args, "--seconds", 120, 10, 3600);
        int iterations = Program.Number(args, "--iterations", 128, 16, 512);
        string browser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        if (!File.Exists(browser)) throw new FileNotFoundException("Microsoft Edge is needed for the graphics workload.", browser);
        string profile = Path.Combine(Path.GetTempPath(), "opencode", $"shubbak-gpu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);
        string token = Guid.NewGuid().ToString("N");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 20));
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var measuring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() =>
        {
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                if (line == "measure") measuring.TrySetResult();
                if (line == "stop") break;
            }
            stop.TrySetResult();
        });
        string html = Page(token, iterations);
        Task server = Serve(listener, token, html, stop.Task, measuring.Task, ready, result, lifetime.Token);
        Process? process = null;
        try
        {
            var monitor = MonitorSource.Enumerate().OrderBy(m => m.IsPrimary).First();
            var info = new ProcessStartInfo(browser)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            foreach (string arg in new[]
            {
                $"--user-data-dir={profile}", $"--app=http://127.0.0.1:{port}/{token}",
                "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-sync",
                "--disable-renderer-backgrounding", "--disable-background-timer-throttling",
                "--window-size=1600,1000", $"--window-position={monitor.Bounds.X + 40},{monitor.Bounds.Y + 40}",
            }) info.ArgumentList.Add(arg);
            process = Process.Start(info) ?? throw new InvalidOperationException("Could not start graphics worker.");
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            string renderer = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token).ConfigureAwait(false);
            if (renderer.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase) ||
                renderer.Contains("software", StringComparison.OrdinalIgnoreCase) ||
                renderer.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) ||
                renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
                renderer == "WebKit WebGL")
                throw new InvalidOperationException($"Graphics workload is software rendered: {renderer}");
            Console.WriteLine("ready");
            await Task.WhenAny(stop.Task, Task.Delay(TimeSpan.FromSeconds(seconds), lifetime.Token)).ConfigureAwait(false);
            stop.TrySetResult();
            string report = await result.Task.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token).ConfigureAwait(false);
            using (JsonDocument measured = JsonDocument.Parse(report))
            {
                if (measured.RootElement.GetProperty("Error").GetInt32() != 0 || measured.RootElement.GetProperty("Frames").GetInt32() == 0)
                    throw new InvalidOperationException($"Graphics workload did not render valid frames: {report}");
            }
            Console.WriteLine(report);
            return 0;
        }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            try
            {
                try { await server.ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
            }
            finally
            {
                if (process is not null)
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow();
                        if (!process.WaitForExit(3000)) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
                    }
                    process.Dispose();
                }
                try { Directory.Delete(profile, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task Serve(TcpListener listener, string token, string html, Task stop, Task measuring,
        TaskCompletionSource<string> ready, TaskCompletionSource<string> result, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using NetworkStream stream = client.GetStream();
            try
            {
                // Parse bytes, not characters: Content-Length counts UTF-8 bytes.
                var headerBytes = new byte[8192];
                int used = 0, end = -1;
                while (end < 0)
                {
                    if (used == headerBytes.Length) throw new IOException("Oversized local header.");
                    int n = await stream.ReadAsync(headerBytes.AsMemory(used), timeout.Token).ConfigureAwait(false);
                    if (n == 0) throw new EndOfStreamException();
                    used += n;
                    end = headerBytes.AsSpan(0, used).IndexOf("\r\n\r\n"u8);
                }
                string[] headers = Encoding.ASCII.GetString(headerBytes, 0, end).Split("\r\n", StringSplitOptions.None);
                int length = 0;
                foreach (string line in headers)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(line.AsSpan(15).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                if (length is < 0 or > 1_000_000) throw new IOException("Oversized local body.");
                var body = new byte[length];
                int copied = Math.Min(used - end - 4, length);
                headerBytes.AsSpan(end + 4, copied).CopyTo(body);
                await stream.ReadExactlyAsync(body.AsMemory(copied), timeout.Token).ConfigureAwait(false);
                string path = headers[0].Split(' ').ElementAtOrDefault(1) ?? "";
                string response = "";
                bool found = true;
                if (path == $"/{token}") response = html;
                else if (path == $"/{token}/ready") ready.TrySetResult(Encoding.UTF8.GetString(body));
                else if (path == $"/{token}/result") result.TrySetResult(Encoding.UTF8.GetString(body));
                else if (path == $"/{token}/status") response = stop.IsCompleted ? "stop" : measuring.IsCompleted ? "measure" : "warmup";
                else found = false;
                byte[] data = Encoding.UTF8.GetBytes(response);
                byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(data, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    private static string Page(string token, int iterations) => $$$"""
        <!doctype html><meta charset="utf-8"><title>Shubbak GPU workload</title>
        <style>body{margin:0;background:#111}canvas{width:100vw;height:100vh}</style>
        <canvas id="view" width="3840" height="2160"></canvas>
        <script>
        const base='/{{{token}}}';
        const gl=document.querySelector('canvas').getContext('webgl2',{antialias:false});
        if(!gl) throw Error('WebGL2 unavailable');
        const dbg=gl.getExtension('WEBGL_debug_renderer_info');
        const renderer=dbg ? gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER);
        function shader(type,text){const s=gl.createShader(type);gl.shaderSource(s,text);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(s));return s;}
        const vs=shader(gl.VERTEX_SHADER,`#version 300 es
        void main(){vec2 p=vec2(float((gl_VertexID<<1)&2),float(gl_VertexID&2));gl_Position=vec4(p*2.0-1.0,0,1);}`);
        const fs=shader(gl.FRAGMENT_SHADER,`#version 300 es
        precision highp float;uniform float phase;out vec4 color;
        void main(){vec2 p=gl_FragCoord.xy/1080.0;float v=0.0;
        for(int i=0;i<{{{iterations}}};i++){p=sin(p.yx*1.01+float(i)*0.1)+cos(p.xy*0.99+phase);v+=sin(dot(p,vec2(12.9898,78.233)));}
        color=vec4(0.45+0.3*sin(vec3(v,v+1.0,v+2.0)),1.0);}`);
        const program=gl.createProgram();gl.attachShader(program,vs);gl.attachShader(program,fs);gl.linkProgram(program);
        if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(program));
        gl.useProgram(program);const phase=gl.getUniformLocation(program,'phase');
        gl.viewport(0,0,3840,2160);
        let last=0,count=0,frames=0,stop=false,measuring=false;const samples=new Float64Array(100000);
        async function watch(){while(!stop){await new Promise(r=>setTimeout(r,500));const state=await(await fetch(base+'/status')).text();stop=state==='stop';if(state==='measure'&&!measuring){measuring=true;last=0;count=0;} } }
        watch();
        function frame(t){
          if(stop){const sorted=Array.from(samples.subarray(0,count)).sort((a,b)=>a-b);
            const at=p=>sorted[Math.max(0,Math.ceil(sorted.length*p)-1)]||0;
            fetch(base+'/result',{method:'POST',body:JSON.stringify({Label:'webgl-frame-cadence',Renderer:renderer,Iterations:{{{iterations}}},Frames:count,Error:gl.getError(),P50Ms:at(.5),P99Ms:at(.99),MaxMs:at(1)})});return;}
          if(measuring&&last&&count<samples.length)samples[count++]=t-last;last=t;
          gl.uniform1f(phase,t*.0001);gl.drawArrays(gl.TRIANGLES,0,3);
          if(++frames===30)fetch(base+'/ready',{method:'POST',body:renderer});
          requestAnimationFrame(frame);
        }
        requestAnimationFrame(frame);
        </script>
        """;
}
