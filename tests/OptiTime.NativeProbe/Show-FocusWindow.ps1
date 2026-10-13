Add-Type -AssemblyName System.Windows.Forms
$focusWindow = New-Object System.Windows.Forms.Form
$focusWindow.Text = 'OptiTime benchmark focus target ' + $PID
$focusWindow.Width = 320
$focusWindow.Height = 120
$focusWindow.StartPosition = 'CenterScreen'
$null = $focusWindow.ShowDialog()
