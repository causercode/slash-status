[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$automationRoot = [System.Windows.Automation.AutomationElement]::RootElement
$windowCondition = [System.Windows.Automation.PropertyCondition]::new(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Window)
$tokenProcesses = @(Get-Process -Name 'TokenStatus' -ErrorAction SilentlyContinue)
$tokenProcessIds = @($tokenProcesses | ForEach-Object { $_.Id })
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

function Format-Bounds {
    param([System.Windows.Rect]$Bounds)

    return '{0},{1} {2}x{3}' -f [int]$Bounds.X, [int]$Bounds.Y, [int]$Bounds.Width, [int]$Bounds.Height
}

function Write-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [int]$Depth
    )

    $current = $Element.Current
    $indent = ' ' * ($Depth * 2)
    $name = if ([string]::IsNullOrWhiteSpace($current.Name)) { '<unnamed>' } else { $current.Name }
    $role = $current.ControlType.ProgrammaticName
    $bounds = Format-Bounds $current.BoundingRectangle
    '{0}{1} | Name="{2}" | Role={3} | Focusable={4} | Enabled={5} | Offscreen={6} | Bounds={7}' -f `
        $indent, $current.AutomationId, $name, $role, $current.IsKeyboardFocusable, $current.IsEnabled, $current.IsOffscreen, $bounds

    $child = $walker.GetFirstChild($Element)
    while ($null -ne $child) {
        Write-Element -Element $child -Depth ($Depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

$windows = $automationRoot.FindAll(
    [System.Windows.Automation.TreeScope]::Children,
    $windowCondition)

$matched = 0
foreach ($window in $windows) {
    $current = $window.Current
    $isTokenStatusProcess = $tokenProcessIds -contains $current.ProcessId
    $isStatusWindow = $current.Name -like '*TokenStatus*' -or $current.Name -like '*/status*'
    if (-not ($isTokenStatusProcess -or $isStatusWindow)) {
        continue
    }

    $matched++
    Write-Element -Element $window -Depth 0
}

if ($matched -eq 0) {
    'No visible TokenStatus windows were found.'
}
