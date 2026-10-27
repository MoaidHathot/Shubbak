namespace Shubbak.Core.Tree;

/// <summary>Normalizes shares while keeping the floor feasible and the total at one.</summary>
internal static class ShareMath
{
    internal static void Normalise(Span<double> shares)
    {
        if (shares.IsEmpty) return;
        double total = 0;
        foreach (double value in shares) total += value;
        if (total <= double.Epsilon) { shares.Fill(1.0 / shares.Length); return; }
        if (Math.Abs(total - 1) < 1e-9) return;
        double floor = Node.MinimumShare(shares.Length);
        double remaining = 1;
        while (true)
        {
            double scale = remaining / total;
            bool fixedAnother = false;
            for (int i = 0; i < shares.Length; i++)
            {
                double value = shares[i];
                if (value < 0 || value * scale >= floor) continue;
                total -= value;
                remaining -= floor;
                shares[i] = -floor;
                fixedAnother = true;
            }
            if (!fixedAnother) break;
        }
        double finalScale = remaining / total;
        for (int i = 0; i < shares.Length; i++)
            shares[i] = shares[i] < 0 ? floor : remaining == 1 ? shares[i] / total : shares[i] * finalScale;
    }
}
