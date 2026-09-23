# Diagnostics

Every complaint the five programs make about a configuration file has a code, so it
can be searched for and talked about. The letters say who is complaining - **SHB** the
window manager's loader and command parser, **TAJ** the bar, **DAL** the palette, **AYN**
the watcher - and `shubbak check-config` runs all four over one file and prints each
with a line, a column and a caret.

An **error** stops the file being loaded: the window manager keeps the configuration
it had, or starts on the built-in one, and says so. A **warning** is a line the loader
could read but suspects - a misspelt key, a name nothing declares, a value it has
repaired - and the file loads with that line read as the message says. The message
templates below show what is filled in as `{name}`; most come with a hint that names
the fix.

This page is generated from the source by `tools/list-diagnostics.ps1`; edit the
code, not the page.

## SHB - The window manager and the command parser

| Code | Severity | Says |
|---|---|---|
| `SHB0001` | Error | Unterminated block comment. |
| `SHB0002` | Error | Unexpected '}' with no matching '{'. |
| `SHB0003` | Error | Expected a node name but found '{Describe(Current)}'. |
| `SHB0004` | Error | Unterminated block: expected '}'. |
| `SHB0005` | Error | Property '{identifier}' has no value. |
| `SHB0006` | Warning | Property '{identifier}' is set more than once; the last value wins. |
| `SHB0007` | Error | Expected a value but found '{Describe(Current)}'. |
| `SHB0008` | Error | Unterminated string. |
| `SHB0009` | Error | A string cannot span lines. |
| `SHB0010` | Warning | Unknown escape sequence '\\{escape}'; the backslash is kept literally. |
| `SHB0011` | Warning | Expected '{' after \\u. |
| `SHB0012` | Warning | Invalid Unicode escape '\\u{{hex}}'. |
| `SHB0013` | Error | Unterminated raw string. |
| `SHB0014` | Error | Blocks are nested more than {MaxDepth} deep; the inner block is ignored. |
| `SHB0113` | Error | Unknown layout '{name}'. |
| `SHB0201` | Error | Empty key binding. |
| `SHB0202` | Error | Binding '{text}' names more than one non-modifier key ('{part}' follows another). |
| `SHB0203` | Error | Unknown key '{part}' in binding '{text}'. |
| `SHB0204` | Error | Binding '{text}' has modifiers but no key. |
| `SHB0301` | Error | Empty command. |
| `SHB0302` | Error | '{text}' does not say which layout to use. |
| `SHB0303` | Error | '{text}' does not name a binding mode. |
| `SHB0304` | Error | shell-exec has nothing to run. |
| `SHB0305` | Error | focus-window takes one window handle. |
| `SHB0306` | Error | '{rest[0]}' is not a window handle. |
| `SHB0307` | Error | signal has no name to announce. |
| `SHB0308` | Error | '{text}' does not say which dimension to resize. |
| `SHB0309` | Error | '{amount}' is not a valid resize amount. |
| `SHB0310` | Error | '{text}' does not name a direction or a monitor. |
| `SHB0311` | Error | '{text}' does not say which workspace to tag to. |
| `SHB0312` | Error | '{text}' has an option scratchpad does not take: {unknown}. |
| `SHB0313` | Error | '{text}' does not say which slot to use. |
| `SHB0314` | Error | '{text}' cannot take --focus. |
| `SHB0315` | Error | '{text}' gives both a direction and a monitor. |
| `SHB0316` | Error | '{text}' does not say which monitor. |
| `SHB0317` | Error | '{text}' asks for more than one of --set, --clear, --toggle and --auto. |
| `SHB0318` | Error | '{text}' does not name a context. |
| `SHB0319` | Error | '{ttlText}' is not a duration. |
| `SHB0320` | Error | '{text}' gives --ttl or --lease with --auto, which has no pin for them to govern. |
| `SHB0321` | Error | '{text}' asks for more than one of --save, --restore and --delete. |
| `SHB0322` | Error | '{text}' does not name an arrangement. |
| `SHB0323` | Error | '{text}' does not say which way to swap. |
| `SHB0324` | Error | '{masters}' is not a master count. |
| `SHB0325` | Error | '{text}' has an option gaps does not take: {unknown}. |
| `SHB0400` | Error | Config file not found: {path} |
| `SHB0401` | Error | Unknown initial window state '{text}'. |
| `SHB0402` | Error | A workspace must be given a name. |
| `SHB0403` | Warning | Workspace '{name}' is declared more than once; the first declaration wins. |
| `SHB0404` | Warning | Unexpected '{child.Name}' inside keybindings; expected 'bind' or 'for-each'. |
| `SHB0405` | Error | Unknown for-each source '{source}'. |
| `SHB0406` | Warning | for-each "workspace" produced no bindings because no workspaces are declared. |
| `SHB0407` | Error | A binding must name a key combination. |
| `SHB0408` | Warning | Binding '{keyText}' runs no commands, so pressing it will do nothing. |
| `SHB0409` | Warning | '{binding.Key.Display}' is bound more than once; the first binding wins. |
| `SHB0410` | Error | A binding mode must be named. |
| `SHB0411` | Error | An app definition must be named. |
| `SHB0412` | Warning | App '{name}' defines no conditions, so it will never match. |
| `SHB0413` | Error | Matcher '{child.Name}' has no pattern. |
| `SHB0414` | Warning | This regex is wrapped in slashes, which are matched literally. |
| `SHB0415` | Error | Invalid regular expression: {ex.Message} |
| `SHB0416` | Error | Rule '{name}' references app '{reference}', which is not defined. |
| `SHB0417` | Error | Rule '{name}' has no conditions, so it would match every window. |
| `SHB0418` | Warning | Rule '{name}' runs no commands. |
| `SHB0419` | Error | '{child.Name}' expects true or false but got '{argument.Raw}'. |
| `SHB0420` | Error | '{name}' expects a whole number but got '{value.Raw}'. |
| `SHB0421` | Warning | Unknown easing curve '{curveName}'; using ease-out. |
| `SHB0422` | Error | Unknown log level '{text}'. |
| `SHB0423` | Error | Unknown hide method '{text}'. |
| `SHB0424` | Error | Unknown setting '{text}' for unmanaged-window-commands. |
| `SHB0425` | Error | Binding mode '{name}' swallows every key and has no binding that leaves it. |
| `SHB0426` | Error | Unknown matcher '{child.Name}'. |
| `SHB0427` | Warning | Unknown top-level section; it will be ignored. |
| `SHB0428` | Warning | Unknown setting in a section ('general', 'gaps', 'window-effects' and the like); it will be ignored. |
| `SHB0429` | Error | Workspace '{name}' asks for unknown layout '{layout}'. |
| `SHB0430` | Error | Workspace '{workspace}' asks for monitor {index}. |
| `SHB0431` | Error | Rule '{name}' has unknown trigger '{triggerName}'. |
| `SHB0432` | Warning | 'repeat' on binding '{keyText}' must be #true or #false. |
| `SHB0433` | Warning | Unknown property '{name}' on binding '{keyText}'; it will be ignored. |
| `SHB0434` | Warning | Binding '{binding.Key.Display}' enters binding mode '{enter.Mode}',  |
| `SHB0435` | Warning | Animation 'fps' of {fps} is outside the supported range  |
| `SHB0436` | Error | Unknown new window placement '{text}'. |
| `SHB0437` | Error | Animation 'fps' must be a number or "auto". |
| `SHB0438` | Error | A monitor definition must be named. |
| `SHB0439` | Error | Unknown monitor matcher '{child.Name}'. |
| `SHB0440` | Warning | Monitor '{name}' defines no conditions, so it will never match. |
| `SHB0441` | Warning | Monitor '{name}' is declared more than once; the first declaration wins. |
| `SHB0442` | Error | Workspace '{workspace}' asks for monitor '{reference}', which is not declared. |
| `SHB0443` | Warning | {where} moves a workspace to monitor '{reference}', which is not declared. |
| `SHB0444` | Error | A context must be named. |
| `SHB0445` | Warning | Context '{name}' is declared more than once; the first declaration wins. |
| `SHB0446` | Error | Unknown condition '{node.Name}' in context '{context}'. |
| `SHB0447` | Error | '{node.Name}' refers to app '{app}', which is not declared. |
| `SHB0448` | Error | Context '{name}' has a negative linger. |
| `SHB0449` | Error | A 'when' block in context '{name}' has no conditions, so it is ignored. |
| `SHB0450` | Error | Context '{contexts[i].Name}' depends on '{reference.Context}', which depends on it; the reference is ignored. |
| `SHB0451` | Warning | {where} refers to context '{reference}', which is not declared. |
| `SHB0452` | Warning | Rule '{name}' runs '{command.Name}' on="{triggerName}", where it does nothing. |
| `SHB0453` | Error | Unknown empty workspace focus '{text}'. |
| `SHB0454` | Error | Workspace '{name}' holds both a double and a single quote, which no command can write. |
| `SHB0455` | Warning | The setting '{node.Name}' has been removed and is ignored. |

## TAJ - The bar

| Code | Severity | Says |
|---|---|---|
| `TAJ0001` | Error | A bar rule must name a profile. |
| `TAJ0002` | Error | Bar rule references profile '{profileName}', which is not defined. |
| `TAJ0003` | Error | A source must be named. |
| `TAJ0004` | Error | A bar profile must be named. |
| `TAJ0005` | Error | A text widget needs a template. |
| `TAJ0006` | Warning | Source '{spec.Name}' is declared more than once. |
| `TAJ0011` | Warning | 'window-manager-timeout' must be a whole number of seconds. |
| `TAJ0012` | Warning | 'window-manager-timeout' cannot be negative ({seconds}). |
| `TAJ0013` | Warning | Unknown setting in 'bar'; it will be ignored. |
| `TAJ0014` | Warning | Unknown setting in a profile; it will be ignored. |
| `TAJ0015` | Warning | Unknown setting in a zone; it will be ignored. |
| `TAJ0016` | Warning | '{id}' is an icon, and an icon has no text to colour; 'colour' will be ignored. |
| `TAJ0017` | Warning | Unknown setting in a 'when' block; it will be ignored. |
| `TAJ0018` | Warning | Unknown setting on a source; it will be ignored. |
| `TAJ0019` | Warning | Unknown setting on a rule; it will be ignored. |
| `TAJ0020` | Warning | Bar rule matches on context '{context}', which the contexts section does not declare;  |
| `TAJ0021` | Warning | '{source}' reads context '{name}', which the contexts section does not declare; it will always be empty. |
| `TAJ0022` | Warning | '{id}' has a hover colour but no on-click; only a clickable widget reacts to the pointer. |
| `TAJ0023` | Warning | '{id}' has {key}="{command}", which the bar cannot perform: {problem} |
| `TAJ0024` | Warning | Unknown backdrop '{text}'; the bar will have none. |
| `TAJ0025` | Warning | '{name}' must be a whole number of pixels. |
| `TAJ0026` | Warning | 'dpi-scaling' should be #true or #false, not '{value.AsString()}'; sizes are scaled to each display. |
| `TAJ0027` | Warning | Source '{name}' has kind="{kind}", which is not one the bar knows; it will produce nothing. |
| `TAJ0028` | Warning | Source '{name}' is a command with no command= to run; it will produce nothing. |
| `TAJ0029` | Warning | Profile extends "{wanted}", which is not a profile declared above it; the built-in defaults are used instead. |
| `TAJ0030` | Warning | '{key}' is "{written}", which is not one of {accepted}; the bar sits at the {fallback.ToString().ToLowerInvariant()}. |
| `TAJ0031` | Warning | '{key}' on {where} is "{text}", which is not a colour; the default is used. |
| `TAJ0032` | Warning | Source '{name}' has culture="{culture}", which is not a culture this machine knows; the invariant culture is used. |

## DAL - The palette

| Code | Severity | Says |
|---|---|---|
| `DAL0001` | Warning | Unknown setting '{child.Name}' in 'dalil'; it will be ignored. |
| `DAL0002` | Warning | '{child.Name}' is not a palette mode; this prefix will be ignored. |
| `DAL0003` | Warning | The prefix for '{child.Name}' must be a single character, not "{spelling}". |
| `DAL0004` | Warning | '{had}' is the prefix for '{PaletteModel.NameOf(mode)}', which will now have none. |
| `DAL0005` | Error | A palette action must be given a name. |
| `DAL0006` | Warning | Palette action '{name}' is declared more than once; both will be listed. |
| `DAL0007` | Error | Palette action '{name}': {wrong.Message} |
| `DAL0008` | Warning | Palette action '{name}' has no commands; it will do nothing. |
| `DAL0009` | Warning | Unknown placement '{value.AsString()}'; the palette will open on the focused monitor. |
| `DAL0010` | Warning | '{value.AsString()}' is not a colour; '{name}' will keep its default. |
| `DAL0011` | Warning | '{name}' must be a whole number; it will keep its default of {fallback}. |
| `DAL0012` | Warning | '{name}' is {given}, outside {low}-{high}; {used} will be used instead. |
| `DAL0013` | Error | Palette action '{name}': nothing declares '{{placeholder}}'. |
| `DAL0014` | Warning | Palette action '{name}' asks for '{parameter.Name}' and never uses it. |
| `DAL0015` | Error | Palette action '{macro}': a param must be given a name. |
| `DAL0016` | Warning | Palette action '{macro}' declares '{name}' more than once; the first will be used. |
| `DAL0017` | Error | Palette action '{macro}': '{from}' is not a list to choose from. |
| `DAL0018` | Warning | The prefix for '{child.Name}' is "{spelling}", a letter or digit; typing it into an empty palette will change mode instead of searching. |

## AYN - The watcher

| Code | Severity | Says |
|---|---|---|
| `AYN0001` | Warning | Unknown setting '{name}' in '{where}'; it will be ignored. |
| `AYN0002` | Warning | '<device> <key>' names no context; the default is used, or the fact is not reported. |
| `AYN0003` | Warning | 'settle' should be a whole number of milliseconds, not '{value.AsString()}';  |
| `AYN0004` | Warning | 'settle' is {milliseconds}; it is kept between 0 and {MaxSettleMilliseconds}, so {clamped} is used. |
| `AYN0005` | Warning | '{where}' names context '{name}', which the contexts section does not declare;  |
| `AYN0006` | Warning | Context '{shared.Key}' is held by more than one fact ({string.Join( |
| `AYN0007` | Warning | '{word} "{shorthand}"' is read as '{word} { {inUseKey} "{shorthand}" }'. |
| `AYN0008` | Warning | '{word} by' needs a program and a context; this rule is ignored. |
| `AYN0009` | Warning | 'renew' should be a whole number of seconds, not '{value.AsString()}'; renewal is off. |
| `AYN0010` | Warning | '{key}' should be a percentage from 1 to 100, not '{value.AsString()}'; {fallback} is used. |
| `AYN0011` | Warning | '{word} device' needs a device name and a context; this rule is ignored. |
