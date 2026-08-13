using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace VisualStudioDebuggerMcp;

internal static class VisualStudioWindowInspector
{
    private const uint GwOwner = 4;
    private const int MaxAutomationElements = 200;

    public static JsonObject Inspect(int processId)
    {
        using var process = Process.GetProcessById(processId);
        process.Refresh();

        var mainWindow = process.MainWindowHandle;
        var mainWindowEnabled = mainWindow == IntPtr.Zero || IsWindowEnabled(mainWindow);
        var activePopup = mainWindow == IntPtr.Zero ? IntPtr.Zero : GetLastActivePopup(mainWindow);
        var topLevelWindows = EnumerateTopLevelWindows(processId);
        var blockingDialogs = new JsonArray();

        foreach (var window in topLevelWindows)
        {
            if (window == mainWindow || !IsWindowVisible(window))
            {
                continue;
            }

            var owner = GetWindow(window, GwOwner);
            var isActivePopup = window == activePopup;
            var isDialogClass = string.Equals(
                GetClassNameValue(window),
                "#32770",
                StringComparison.Ordinal);

            if (!mainWindowEnabled &&
                (isActivePopup || (isDialogClass && IsWindowEnabled(window))))
            {
                blockingDialogs.Add(
                    DescribeWindow(window, owner, isActivePopup, includeAutomationDetails: true));
            }
        }

        return new JsonObject
        {
            ["mainWindow"] = mainWindow == IntPtr.Zero
                ? null
                : DescribeWindow(
                    mainWindow,
                    IntPtr.Zero,
                    isActivePopup: false,
                    includeAutomationDetails: false),
            ["mainWindowEnabled"] = mainWindowEnabled,
            ["hasBlockingDialog"] = blockingDialogs.Count > 0,
            ["blockingDialogs"] = blockingDialogs
        };
    }

    public static JsonObject ClickButton(
        int processId,
        string buttonName,
        string? dialogTitle)
    {
        var state = Inspect(processId);
        if (state["blockingDialogs"] is not JsonArray dialogs || dialogs.Count == 0)
        {
            throw new InvalidOperationException(
                $"Visual Studio process {processId} has no blocking modal dialog.");
        }

        var matchingDialogs = dialogs
            .OfType<JsonObject>()
            .Where(dialog =>
                string.IsNullOrWhiteSpace(dialogTitle) ||
                string.Equals(
                    dialog["title"]?.GetValue<string>(),
                    dialogTitle,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingDialogs.Count != 1)
        {
            throw new InvalidOperationException(
                matchingDialogs.Count == 0
                    ? $"No blocking dialog matched title '{dialogTitle}'."
                    : "Multiple blocking dialogs matched. Specify an exact dialog title.");
        }

        var dialog = matchingDialogs[0];
        var handleText = dialog["handle"]?.GetValue<string>()
            ?? throw new InvalidOperationException("The blocking dialog has no window handle.");
        var window = new IntPtr(Convert.ToInt64(handleText[2..], 16));
        var root = AutomationElement.FromHandle(window);
        var elements = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.ControlTypeProperty,
                ControlType.Button));
        var normalizedButtonName = NormalizeButtonName(buttonName);
        var matches = new List<AutomationElement>();
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            if (string.Equals(
                NormalizeButtonName(element.Current.Name),
                normalizedButtonName,
                StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(element);
            }
        }

        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                matches.Count == 0
                    ? $"Dialog '{dialog["title"]}' has no button named '{buttonName}'."
                    : $"Dialog '{dialog["title"]}' has multiple buttons named '{buttonName}'.");
        }

        var button = matches[0];
        if (!button.Current.IsEnabled)
        {
            throw new InvalidOperationException(
                $"Dialog button '{button.Current.Name}' is disabled.");
        }

        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) ||
            pattern is not InvokePattern invokePattern)
        {
            throw new InvalidOperationException(
                $"Dialog button '{button.Current.Name}' does not support invocation.");
        }

        var clickedDialogTitle = dialog["title"]?.GetValue<string>();
        var clickedButtonName = button.Current.Name;
        invokePattern.Invoke();
        return new JsonObject
        {
            ["clicked"] = true,
            ["processId"] = processId,
            ["dialogTitle"] = clickedDialogTitle,
            ["buttonName"] = clickedButtonName
        };
    }

    private static JsonObject DescribeWindow(
        IntPtr window,
        IntPtr owner,
        bool isActivePopup,
        bool includeAutomationDetails)
    {
        var description = new JsonObject
        {
            ["handle"] = FormatHandle(window),
            ["ownerHandle"] = owner == IntPtr.Zero ? null : FormatHandle(owner),
            ["title"] = GetWindowTextValue(window),
            ["className"] = GetClassNameValue(window),
            ["enabled"] = IsWindowEnabled(window),
            ["isActivePopup"] = isActivePopup
        };

        if (includeAutomationDetails)
        {
            AddAutomationDetails(window, description);
        }

        return description;
    }

    private static void AddAutomationDetails(IntPtr window, JsonObject description)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var text = new JsonArray();
            var buttons = new JsonArray();
            var seenText = new HashSet<string>(StringComparer.Ordinal);

            for (var index = 0; index < Math.Min(elements.Count, MaxAutomationElements); index++)
            {
                var element = elements[index];
                string name;
                ControlType controlType;
                bool enabled;
                try
                {
                    name = element.Current.Name?.Trim() ?? string.Empty;
                    controlType = element.Current.ControlType;
                    enabled = element.Current.IsEnabled;
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (controlType == ControlType.Button)
                {
                    buttons.Add(new JsonObject
                    {
                        ["name"] = name,
                        ["enabled"] = enabled,
                        ["automationId"] = element.Current.AutomationId
                    });
                }
                else if (
                    controlType == ControlType.Text ||
                    controlType == ControlType.Document ||
                    controlType == ControlType.Group)
                {
                    if (seenText.Add(name))
                    {
                        text.Add(name);
                    }
                }
            }

            description["text"] = text;
            description["buttons"] = buttons;
            description["automationElementsTruncated"] = elements.Count > MaxAutomationElements;
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or
            InvalidOperationException or
            COMException)
        {
            description["automationError"] = exception.Message;
        }
    }

    private static List<IntPtr> EnumerateTopLevelWindows(int processId)
    {
        var windows = new List<IntPtr>();
        EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out var ownerProcessId);
                if (ownerProcessId == processId)
                {
                    windows.Add(window);
                }

                return true;
            },
            IntPtr.Zero);
        return windows;
    }

    private static string GetWindowTextValue(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        var text = new StringBuilder(length + 1);
        _ = GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private static string GetClassNameValue(IntPtr window)
    {
        var className = new StringBuilder(256);
        _ = GetClassName(window, className, className.Capacity);
        return className.ToString();
    }

    private static string FormatHandle(IntPtr handle) =>
        $"0x{handle.ToInt64():X}";

    private static string NormalizeButtonName(string value) =>
        value.Replace("&", string.Empty, StringComparison.Ordinal).Trim();

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out int processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(
        IntPtr window,
        uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr window,
        StringBuilder text,
        int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window,
        StringBuilder className,
        int maxCount);
}
