#requires -Version 5.1
<#
.SYNOPSIS
Starts a synthetic, local Windows UI automation target.
.DESCRIPTION
Run with system Windows PowerShell 5.1 -STA, not pwsh. The caller supplies a
unique visible title and a new artifact path, and owns the returned OS process.
StatePath is written, plus a new text file in its directory only when the
Workbench's real Save As dialog succeeds. No contacts, orders, notifications,
or preferences leave this process. State is recorded by fixture events, not
by reading the controls externally. The original tabs and TextDocument option
retain their names and layout; Workbench, Workflow, Scroll, and Transfer are
additional disposable tasks.
.EXAMPLE
powershell.exe -NoProfile -STA -File .\Start-UIAutomationFixture.ps1 -Title "Lumi UI Bench manual-001" -StatePath C:\Temp\bench-001\state.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Title,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$StatePath,

    [switch]$TextDocument
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
if ($PSVersionTable.PSEdition -ne 'Desktop' -or
    [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Use system Windows PowerShell 5.1 with -STA.'
}

$script:artifactPath = [IO.Path]::GetFullPath($StatePath)
if ([IO.File]::Exists($script:artifactPath)) {
    throw 'StatePath must be new; refusing to overwrite an earlier fixture.'
}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($script:artifactPath))
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
[Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)

$script:state = [ordered]@{
    schemaVersion = 1
    title = $Title
    processId = [Diagnostics.Process]::GetCurrentProcess().Id
    startedAt = [DateTimeOffset]::UtcNow.ToString('o')
    ready = $false
    closed = $false
    sequence = 0
    lastEvent = 'created'
    order = [ordered]@{ submitted = $false; submissionCount = 0 }
    preferences = [ordered]@{ saved = $false; saveCount = 0 }
    catalog = [ordered]@{
        totalRows = 200
        selectionCount = 0
        selectedId = $null
        selectedLabel = $null
        committedId = $null
        committedLabel = $null
        commitCount = 0
    }
    document = [ordered]@{
        saved = $false; saveCount = 0; fileName = $null; filePath = $null
        textLength = 0; lineCount = 0; encoding = $null; saveError = $null
    }
    delayed = [ordered]@{
        loadCount = 0; loadCompleted = $false; applied = $false; applyCount = 0
        reportText = $null; loadStartedAt = $null; loadCompletedAt = $null; appliedAt = $null
    }
    scroll = [ordered]@{
        totalRows = 200; initialTopIndex = 0; topIndex = 0; maxTopIndex = 0
        scrollObserved = $false; lastRowVisible = $false
        lastRowTop = $null; lastRowBottom = $null; viewportHeight = $null
        confirmed = $false; confirmCount = 0; confirmedTopIndex = $null; confirmedLastRowVisible = $false
    }
    transfer = [ordered]@{
        sourceReference = 'REF-7328'; destinationReference = $null
        opened = $false; windowOpen = $false; windowTitle = $null
        committed = $false; commitCount = 0; error = $null
    }
    tree = [ordered]@{ selectedPath = $null; assignedPath = $null; assignCount = 0; expandCount = 0; checkedList = ''; checkCount = 0 }
    grid = [ordered]@{
        totalRows = 60; saveCount = 0; changedList = ''; targetStatus = 'Open'
        targetCustomer = 'Contoso Pharmacy'; targetAmount = '2,318.40'
    }
    wizard = [ordered]@{
        opened = $false; completed = $false; finishCount = 0; cancelled = $false
        plan = $null; seats = $null; storageGb = $null; acceptedTerms = $false
    }
    account = [ordered]@{
        created = $false; createdCount = 0; rejectedCount = 0; invalidCount = 0
        username = $null; email = $null; age = $null
    }
    files = [ordered]@{
        archivedList = ''; archiveCount = 0; deletedList = ''; deleteCount = 0
        openedList = ''; openCount = 0; declinedCount = 0
    }
}

function Save-FixtureState {
    param([string]$EventName)
    $script:state.sequence++
    $script:state.lastEvent = $EventName
    $json = ConvertTo-Json -InputObject $script:state -Depth 8
    [IO.File]::WriteAllText($script:artifactPath, $json, [Text.UTF8Encoding]::new($false))
}

function Add-Label {
    param($Parent, [string]$Text, [int]$X, [int]$Y, [int]$Width = 150)
    $label = [Windows.Forms.Label]::new()
    $label.Text = $Text
    $label.Name = 'Label' + ($Text -replace '[^A-Za-z0-9]', '')
    $label.AccessibleName = $Text
    $label.Location = [Drawing.Point]::new($X, $Y)
    $label.Size = [Drawing.Size]::new($Width, 27)
    $Parent.Controls.Add($label)
}

function Add-Field {
    param($Parent, [string]$Label, [int]$Y, [string[]]$Choices)
    Add-Label $Parent $Label 20 ($Y + 4)
    if ($null -ne $Choices -and $Choices.Count -gt 0) {
        $control = [Windows.Forms.ComboBox]::new()
        $control.DropDownStyle = [Windows.Forms.ComboBoxStyle]::DropDownList
        foreach ($choice in $Choices) { [void]$control.Items.Add($choice) }
        $control.SelectedIndex = 0
    }
    else {
        $control = [Windows.Forms.TextBox]::new()
    }
    $control.Name = $Label -replace '[^A-Za-z0-9]', ''
    $control.AccessibleName = $Label
    $control.Location = [Drawing.Point]::new(180, $Y)
    $control.Size = [Drawing.Size]::new(470, 28)
    $control.TabIndex = $Parent.Controls.Count
    $Parent.Controls.Add($control)
    return $control
}

function Add-Button {
    param($Parent, [string]$Text, [int]$X, [int]$Y, [int]$Width = 155)
    $button = [Windows.Forms.Button]::new()
    $button.Name = $Text -replace '[^A-Za-z0-9]', ''
    $button.AccessibleName = $Text
    $button.Text = $Text
    $button.Location = [Drawing.Point]::new($X, $Y)
    $button.Size = [Drawing.Size]::new($Width, 34)
    $button.TabIndex = $Parent.Controls.Count
    $Parent.Controls.Add($button)
    return $button
}

function Add-CheckBox {
    param($Parent, [string]$Text, [int]$X, [int]$Y, [bool]$Checked = $false)
    $checkBox = [Windows.Forms.CheckBox]::new()
    $checkBox.Text = $Text
    $checkBox.Name = $Text -replace '[^A-Za-z0-9]', ''
    $checkBox.AccessibleName = $Text
    $checkBox.Checked = $Checked
    $checkBox.Location = [Drawing.Point]::new($X, $Y)
    $checkBox.Size = [Drawing.Size]::new(360, 30)
    $checkBox.TabIndex = $Parent.Controls.Count
    $Parent.Controls.Add($checkBox)
    return $checkBox
}

function Add-Status {
    param($Parent, [string]$Name, [string]$Text, [int]$Y, [int]$Height = 42)
    $label = [Windows.Forms.Label]::new()
    $label.Name = $Name
    $label.Text = $Text
    $label.AccessibleName = $Text
    $label.Location = [Drawing.Point]::new(20, $Y)
    $label.Size = [Drawing.Size]::new(675, $Height)
    $Parent.Controls.Add($label)
    return $label
}

function Set-Status {
    param($Label, [string]$Text)
    $Label.Text = $Text
    $Label.AccessibleName = $Text
}

$script:mainForm = [Windows.Forms.Form]::new()
$script:mainForm.Text = $Title
$script:mainForm.Name = 'AutomationFixture'
$script:mainForm.AccessibleName = $Title
$script:mainForm.Font = [Drawing.Font]::new('Segoe UI', 10)
$script:mainForm.ClientSize = [Drawing.Size]::new(760, 650)
$script:mainForm.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen
$script:mainForm.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
$script:mainForm.MaximizeBox = $false
$script:mainForm.AutoScaleMode = [Windows.Forms.AutoScaleMode]::Dpi
Add-Label $script:mainForm 'Demo order desk - synthetic data only' 20 20 475
$preferencesButton = Add-Button $script:mainForm 'Preferences' 572 12

$tabs = [Windows.Forms.TabControl]::new()
$tabs.Name = 'WorkspaceTabs'
$tabs.AccessibleName = 'Workspace'
$tabs.Location = [Drawing.Point]::new(16, 60)
$tabs.Size = [Drawing.Size]::new(728, 542)
$tabs.TabIndex = 1
$script:mainForm.Controls.Add($tabs)
$orderTab = [Windows.Forms.TabPage]::new('Order')
$orderTab.Name = 'OrderTab'
$orderTab.AccessibleName = 'Order'
$catalogTab = [Windows.Forms.TabPage]::new('Catalog')
$catalogTab.Name = 'CatalogTab'
$catalogTab.AccessibleName = 'Catalog'
$tabs.TabPages.Add($orderTab)
$tabs.TabPages.Add($catalogTab)
if ($TextDocument) {
    $documentTab = [Windows.Forms.TabPage]::new('Document')
    $editor = [Windows.Forms.RichTextBox]::new()
    $editor.Name = 'TestDocument'
    $editor.AccessibleName = 'Test document'
    $editor.Dock = [Windows.Forms.DockStyle]::Fill
    $documentTab.Controls.Add($editor)
    $tabs.TabPages.Add($documentTab)
    $tabs.SelectedTab = $documentTab
}
$workbenchTab = [Windows.Forms.TabPage]::new('Workbench')
$workbenchTab.Name = 'WorkbenchTab'
$workbenchTab.AccessibleName = 'Workbench'
$script:workflowTab = [Windows.Forms.TabPage]::new('Workflow')
$script:workflowTab.Name = 'WorkflowTab'
$script:workflowTab.AccessibleName = 'Workflow'
$scrollTab = [Windows.Forms.TabPage]::new('Scroll')
$scrollTab.Name = 'ScrollTab'
$scrollTab.AccessibleName = 'Scroll'
$transferTab = [Windows.Forms.TabPage]::new('Transfer')
$transferTab.Name = 'TransferTab'
$transferTab.AccessibleName = 'Transfer'
$tabs.TabPages.Add($workbenchTab)
$tabs.TabPages.Add($script:workflowTab)
$tabs.TabPages.Add($scrollTab)
$tabs.TabPages.Add($transferTab)
$treeTab = [Windows.Forms.TabPage]::new('Tree')
$treeTab.Name = 'TreeTab'
$treeTab.AccessibleName = 'Tree'
$gridTab = [Windows.Forms.TabPage]::new('Grid')
$gridTab.Name = 'GridTab'
$gridTab.AccessibleName = 'Grid'
$setupTab = [Windows.Forms.TabPage]::new('Setup')
$setupTab.Name = 'SetupTab'
$setupTab.AccessibleName = 'Setup'
$accountTab = [Windows.Forms.TabPage]::new('Account')
$accountTab.Name = 'AccountTab'
$accountTab.AccessibleName = 'Account'
$filesTab = [Windows.Forms.TabPage]::new('Files')
$filesTab.Name = 'FilesTab'
$filesTab.AccessibleName = 'Files'
foreach ($page in @($treeTab, $gridTab, $setupTab, $accountTab, $filesTab)) { $tabs.TabPages.Add($page) }

$script:orderFields = @{}
$row = 20
foreach ($label in @('First name', 'Last name', 'Email', 'Company', 'City')) {
    $script:orderFields[$label] = Add-Field $orderTab $label $row
    $row += 39
}
$script:orderFields['Product'] = Add-Field $orderTab 'Product' $row @('Desk lamp', 'Travel mug', 'Notebook')
$row += 39
$script:orderFields['Quantity'] = Add-Field $orderTab 'Quantity' $row
$script:orderFields['Quantity'].Text = '1'
$row += 39
$script:orderFields['Delivery'] = Add-Field $orderTab 'Delivery' $row @('Standard', 'Express', 'Collection')
$script:receiptCheckBox = Add-CheckBox $orderTab 'Send receipt' 180 341
$submitButton = Add-Button $orderTab 'Submit order' 180 391
$script:orderStatus = [Windows.Forms.Label]::new()
$script:orderStatus.Name = 'OrderStatus'
$script:orderStatus.AccessibleName = 'Order status'
$script:orderStatus.Text = 'No order submitted.'
$script:orderStatus.Location = [Drawing.Point]::new(20, 448)
$script:orderStatus.Size = [Drawing.Size]::new(675, 42)
$orderTab.Controls.Add($script:orderStatus)
$submitButton.Add_Click({
    $quantity = 0
    $missing = @($script:orderFields.Keys | Where-Object {
        [string]::IsNullOrWhiteSpace($script:orderFields[$_].Text)
    })
    if ($missing.Count -gt 0 -or
        -not [int]::TryParse($script:orderFields['Quantity'].Text, [ref]$quantity) -or
        $quantity -lt 1 -or $quantity -gt 99) {
        $script:orderStatus.Text = 'Complete every field; quantity must be from 1 to 99.'
        return
    }
    $script:state.order = [ordered]@{
        submitted = $true
        submissionCount = $script:state.order.submissionCount + 1
        firstName = $script:orderFields['First name'].Text
        lastName = $script:orderFields['Last name'].Text
        email = $script:orderFields['Email'].Text
        company = $script:orderFields['Company'].Text
        city = $script:orderFields['City'].Text
        product = $script:orderFields['Product'].Text
        quantity = $quantity
        delivery = $script:orderFields['Delivery'].Text
        sendReceipt = $script:receiptCheckBox.Checked
    }
    Save-FixtureState 'order-submitted'
    $script:orderStatus.Text = 'Order submitted for ' + $script:state.order.firstName + ' ' + $script:state.order.lastName + '.'
})

$script:catalogRows = @(1..200 | ForEach-Object {
    if ($_ -eq 187) { 'SKU-187 - Cedar travel case' }
    else { 'SKU-{0:D3} - Sample item {0:D3}' -f $_ }
})
Add-Label $catalogTab 'Search catalog' 20 24
$script:catalogSearch = [Windows.Forms.TextBox]::new()
$script:catalogSearch.Name = 'SearchCatalog'
$script:catalogSearch.AccessibleName = 'Search catalog'
$script:catalogSearch.Location = [Drawing.Point]::new(180, 20)
$script:catalogSearch.Size = [Drawing.Size]::new(340, 28)
$script:catalogSearch.TabIndex = 0
$catalogTab.Controls.Add($script:catalogSearch)
$searchButton = Add-Button $catalogTab 'Search' 540 17 130
$script:catalogList = [Windows.Forms.ListBox]::new()
$script:catalogList.Name = 'CatalogItems'
$script:catalogList.AccessibleName = 'Catalog items'
$script:catalogList.Location = [Drawing.Point]::new(20, 67)
$script:catalogList.Size = [Drawing.Size]::new(650, 340)
$script:catalogList.SelectionMode = [Windows.Forms.SelectionMode]::One
$script:catalogList.TabIndex = 2
foreach ($item in $script:catalogRows) { [void]$script:catalogList.Items.Add($item) }
$catalogTab.Controls.Add($script:catalogList)
$commitButton = Add-Button $catalogTab 'Use selected item' 20 421 185
$script:catalogStatus = [Windows.Forms.Label]::new()
$script:catalogStatus.Name = 'CatalogStatus'
$script:catalogStatus.AccessibleName = 'Catalog status'
$script:catalogStatus.Text = '200 items. No item committed.'
$script:catalogStatus.Location = [Drawing.Point]::new(20, 464)
$script:catalogStatus.Size = [Drawing.Size]::new(675, 35)
$catalogTab.Controls.Add($script:catalogStatus)
$searchButton.Add_Click({
    $query = $script:catalogSearch.Text.Trim()
    $script:catalogList.BeginUpdate()
    try {
        $script:catalogList.Items.Clear()
        foreach ($item in $script:catalogRows) {
            if ($item.IndexOf($query, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                [void]$script:catalogList.Items.Add($item)
            }
        }
    }
    finally { $script:catalogList.EndUpdate() }
    $script:catalogStatus.Text = [string]$script:catalogList.Items.Count + ' matching items.'
})
$script:catalogList.Add_SelectedIndexChanged({
    $selection = [string]$script:catalogList.SelectedItem
    $script:state.catalog.selectionCount++
    $script:state.catalog.selectedLabel = if ($selection) { $selection } else { $null }
    $script:state.catalog.selectedId = if ($selection) { $selection.Substring(0, 7) } else { $null }
    Save-FixtureState 'catalog-selection'
})
$commitButton.Add_Click({
    if ($script:catalogList.SelectedIndex -lt 0) {
        $script:catalogStatus.Text = 'Select an item first.'
        return
    }
    $selection = [string]$script:catalogList.SelectedItem
    $script:state.catalog.selectedId = $selection.Substring(0, 7)
    $script:state.catalog.selectedLabel = $selection
    $script:state.catalog.committedId = $selection.Substring(0, 7)
    $script:state.catalog.committedLabel = $selection
    $script:state.catalog.commitCount++
    Save-FixtureState 'catalog-committed'
    $script:catalogStatus.Text = 'Committed: ' + $selection
})

$script:mainStatus = [Windows.Forms.Label]::new()
$script:mainStatus.Name = 'PreferencesStatus'
$script:mainStatus.AccessibleName = 'Preferences status'
$script:mainStatus.Text = 'Preferences have not been saved.'
$script:mainStatus.Location = [Drawing.Point]::new(20, 612)
$script:mainStatus.Size = [Drawing.Size]::new(710, 30)
$script:mainForm.Controls.Add($script:mainStatus)
$preferencesButton.Add_Click({
    $script:preferencesDialog = [Windows.Forms.Form]::new()
    $script:preferencesDialog.Text = 'Preferences - ' + $script:mainForm.Text
    $script:preferencesDialog.Name = 'PreferencesDialog'
    $script:preferencesDialog.AccessibleName = $script:preferencesDialog.Text
    $script:preferencesDialog.Font = $script:mainForm.Font
    $script:preferencesDialog.ClientSize = [Drawing.Size]::new(690, 360)
    $script:preferencesDialog.StartPosition = [Windows.Forms.FormStartPosition]::CenterParent
    $script:preferencesDialog.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
    $script:preferencesDialog.MinimizeBox = $false
    $script:preferencesDialog.MaximizeBox = $false
    $script:preferencesDialog.ShowInTaskbar = $false
    $script:preferenceFields = @{
        displayName = (Add-Field $script:preferencesDialog 'Display name' 20)
        language = (Add-Field $script:preferencesDialog 'Language' 63 @('English', 'French', 'Spanish'))
        theme = (Add-Field $script:preferencesDialog 'Theme' 106 @('System', 'Light', 'Dark'))
        reminderTime = (Add-Field $script:preferencesDialog 'Reminder time' 149 @('08:00', '09:30', '16:00'))
        compactLayout = (Add-CheckBox $script:preferencesDialog 'Compact layout' 180 195)
        desktopNotifications = (Add-CheckBox $script:preferencesDialog 'Desktop notifications' 180 230 $true)
    }
    $script:preferenceFields.reminderTime.DropDownStyle = [Windows.Forms.ComboBoxStyle]::DropDown
    if ($script:state.preferences.saved) {
        foreach ($key in @('displayName', 'language', 'theme', 'reminderTime')) {
            $script:preferenceFields[$key].Text = $script:state.preferences[$key]
        }
        $script:preferenceFields.compactLayout.Checked = $script:state.preferences.compactLayout
        $script:preferenceFields.desktopNotifications.Checked = $script:state.preferences.desktopNotifications
    }
    $saveButton = Add-Button $script:preferencesDialog 'Save preferences' 180 293 185
    $cancelButton = Add-Button $script:preferencesDialog 'Cancel' 380 293 120
    $cancelButton.DialogResult = [Windows.Forms.DialogResult]::Cancel
    $script:preferencesDialog.AcceptButton = $saveButton
    $script:preferencesDialog.CancelButton = $cancelButton
    $saveButton.Add_Click({
        $script:state.preferences = [ordered]@{
            saved = $true
            saveCount = $script:state.preferences.saveCount + 1
            displayName = $script:preferenceFields.displayName.Text
            language = $script:preferenceFields.language.Text
            theme = $script:preferenceFields.theme.Text
            reminderTime = $script:preferenceFields.reminderTime.Text
            compactLayout = $script:preferenceFields.compactLayout.Checked
            desktopNotifications = $script:preferenceFields.desktopNotifications.Checked
        }
        Save-FixtureState 'preferences-saved'
        $script:mainStatus.Text = 'Preferences saved.'
        $script:preferencesDialog.DialogResult = [Windows.Forms.DialogResult]::OK
    })
    try { [void]$script:preferencesDialog.ShowDialog($script:mainForm) }
    finally { $script:preferencesDialog.Dispose() }
})

$documentMenu = [Windows.Forms.MenuStrip]::new()
$documentMenu.Name = 'DocumentMenu'
$fileMenu = [Windows.Forms.ToolStripMenuItem]::new('File')
$saveAsMenu = [Windows.Forms.ToolStripMenuItem]::new('Save as...')
$saveAsMenu.Name = 'SaveDocumentAs'
$saveAsMenu.AccessibleName = 'Save as'
$saveAsMenu.ShortcutKeys = [Windows.Forms.Keys]::Control -bor [Windows.Forms.Keys]::Shift -bor [Windows.Forms.Keys]::S
[void]$fileMenu.DropDownItems.Add($saveAsMenu)
[void]$documentMenu.Items.Add($fileMenu)
$workbenchTab.Controls.Add($documentMenu)
$script:mainForm.MainMenuStrip = $documentMenu
Add-Label $workbenchTab 'Document text' 20 43 650
$script:documentEditor = [Windows.Forms.TextBox]::new()
$script:documentEditor.Name = 'DocumentText'
$script:documentEditor.AccessibleName = 'Document text'
$script:documentEditor.Multiline = $true
$script:documentEditor.AcceptsReturn = $true
$script:documentEditor.ScrollBars = [Windows.Forms.ScrollBars]::Vertical
$script:documentEditor.Location = [Drawing.Point]::new(20, 76)
$script:documentEditor.Size = [Drawing.Size]::new(650, 335)
$workbenchTab.Controls.Add($script:documentEditor)
$script:documentStatus = [Windows.Forms.Label]::new()
$script:documentStatus.Name = 'DocumentStatus'
$script:documentStatus.Text = 'Not saved. Use File > Save as to save a new text file.'
$script:documentStatus.AccessibleName = $script:documentStatus.Text
$script:documentStatus.Location = [Drawing.Point]::new(20, 430)
$script:documentStatus.Size = [Drawing.Size]::new(675, 60)
$workbenchTab.Controls.Add($script:documentStatus)
$saveAsMenu.Add_Click({
    $dialog = [Windows.Forms.SaveFileDialog]::new()
    $dialog.Title = 'Save as - ' + $script:mainForm.Text
    $dialog.InitialDirectory = [IO.Path]::GetDirectoryName($script:artifactPath)
    $dialog.FileName = 'Untitled.txt'
    $dialog.Filter = 'Text files (*.txt)|*.txt'
    $dialog.DefaultExt = 'txt'
    $dialog.RestoreDirectory = $true
    $dialog.OverwritePrompt = $true
    try {
        if ($dialog.ShowDialog($script:mainForm) -ne [Windows.Forms.DialogResult]::OK) { return }
        $path = [IO.Path]::GetFullPath($dialog.FileName)
        if (-not [string]::Equals([IO.Path]::GetDirectoryName($path),
            [IO.Path]::GetDirectoryName($script:artifactPath), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Save only in the initial fixture directory; no file was written.'
        }
        if ([IO.File]::Exists($path)) { throw 'Choose a new filename; existing files are never overwritten.' }
        $text = $script:documentEditor.Text
        [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
        $script:state.document.saved = $true
        $script:state.document.saveCount++
        $script:state.document.fileName = [IO.Path]::GetFileName($path)
        $script:state.document.filePath = $path
        $script:state.document.textLength = $text.Length
        $script:state.document.lineCount = @($text -split '\r\n|\r|\n').Count
        $script:state.document.encoding = 'utf-8'
        $script:state.document.saveError = $null
        $script:documentStatus.Text = 'Saved: ' + $script:state.document.fileName
        Save-FixtureState 'document-saved'
    }
    catch {
        $script:state.document.saveError = $_.Exception.Message
        $script:documentStatus.Text = 'Save failed: ' + $_.Exception.Message
        Save-FixtureState 'document-save-failed'
    }
    finally {
        $script:documentStatus.AccessibleName = $script:documentStatus.Text
        $dialog.Dispose()
    }
})

$script:loadReportButton = Add-Button $script:workflowTab 'Load report' 20 20
$script:applyReportButton = Add-Button $script:workflowTab 'Apply report' 20 222
$script:applyReportButton.Enabled = $false
$script:reportStatus = [Windows.Forms.Label]::new()
$script:reportStatus.Name = 'ReportStatus'
$script:reportStatus.Text = 'Report not loaded.'
$script:reportStatus.AccessibleName = $script:reportStatus.Text
$script:reportStatus.Location = [Drawing.Point]::new(20, 82)
$script:reportStatus.Size = [Drawing.Size]::new(650, 42)
$script:workflowTab.Controls.Add($script:reportStatus)
$script:reportTimer = [Windows.Forms.Timer]::new()
$script:reportTimer.Interval = 700
$script:loadReportButton.Add_Click({
    $script:loadReportButton.Enabled = $false
    $script:applyReportButton.Enabled = $false
    $script:state.delayed.loadCount++
    $script:state.delayed.loadStartedAt = [DateTimeOffset]::UtcNow.ToString('o')
    $script:reportStatus.Text = 'Loading report. Apply report is unavailable until ready.'
    $script:reportStatus.AccessibleName = $script:reportStatus.Text
    Save-FixtureState 'report-loading'
    $script:reportTimer.Start()
})
$script:reportTimer.Add_Tick({
    $script:reportTimer.Stop()
    $resultLabel = [Windows.Forms.Label]::new()
    $resultLabel.Name = 'ReportResult'
    $resultLabel.Text = 'Report ready: 12 orders; total 480.'
    $resultLabel.AccessibleName = $resultLabel.Text
    $resultLabel.Location = [Drawing.Point]::new(20, 146)
    $resultLabel.Size = [Drawing.Size]::new(650, 42)
    $script:workflowTab.Controls.Add($resultLabel)
    $script:state.delayed.reportText = $resultLabel.Text
    $script:state.delayed.loadCompleted = $true
    $script:state.delayed.loadCompletedAt = [DateTimeOffset]::UtcNow.ToString('o')
    $script:applyReportButton.Enabled = $true
    $script:reportStatus.Text = 'Report ready. Apply report is now available.'
    $script:reportStatus.AccessibleName = $script:reportStatus.Text
    Save-FixtureState 'report-ready'
})
$script:applyReportButton.Add_Click({
    if (-not $script:state.delayed.loadCompleted) {
        $script:reportStatus.Text = 'Cannot apply: wait for the report to finish loading.'
        $script:reportStatus.AccessibleName = $script:reportStatus.Text
        return
    }
    $script:applyReportButton.Enabled = $false
    $script:state.delayed.applied = $true
    $script:state.delayed.applyCount++
    $script:state.delayed.appliedAt = [DateTimeOffset]::UtcNow.ToString('o')
    $script:reportStatus.Text = 'Report applied.'
    $script:reportStatus.AccessibleName = $script:reportStatus.Text
    Save-FixtureState 'report-applied'
})

Add-Label $scrollTab 'Scroll to the end of this 200-entry report, then acknowledge it.' 20 20 675
$script:scrollList = [Windows.Forms.ListBox]::new()
$script:scrollList.Name = 'ReportEntries'
$script:scrollList.AccessibleName = 'Report entries'
$script:scrollList.Location = [Drawing.Point]::new(20, 62)
$script:scrollList.Size = [Drawing.Size]::new(650, 340)
$script:scrollList.IntegralHeight = $false
$script:scrollList.SelectionMode = [Windows.Forms.SelectionMode]::None
foreach ($rowNumber in 1..200) {
    $rowText = if ($rowNumber -eq 200) { 'Entry 200 - End of report' } else { 'Entry {0:D3} - Report detail' -f $rowNumber }
    [void]$script:scrollList.Items.Add($rowText)
}
$scrollTab.Controls.Add($script:scrollList)
$script:confirmEndButton = Add-Button $scrollTab 'Confirm end' 20 417
$script:confirmEndButton.Visible = $false
$script:scrollStatus = [Windows.Forms.Label]::new()
$script:scrollStatus.Name = 'ScrollStatus'
$script:scrollStatus.Text = 'Scroll down until entry 200 is fully visible.'
$script:scrollStatus.AccessibleName = $script:scrollStatus.Text
$script:scrollStatus.Location = [Drawing.Point]::new(20, 462)
$script:scrollStatus.Size = [Drawing.Size]::new(675, 42)
$scrollTab.Controls.Add($script:scrollStatus)

function Update-ScrollState {
    if (-not $script:scrollList.IsHandleCreated -or -not $script:scrollList.Visible) { return $false }
    $topIndex = $script:scrollList.TopIndex
    $lastRow = $script:scrollList.GetItemRectangle($script:scrollList.Items.Count - 1)
    $height = $script:scrollList.ClientSize.Height
    $lastVisible = $lastRow.Height -gt 0 -and $lastRow.Top -ge 0 -and $lastRow.Bottom -le $height
    $changed = $topIndex -ne $script:state.scroll.topIndex -or $lastVisible -ne $script:state.scroll.lastRowVisible
    $script:state.scroll.topIndex = $topIndex
    $script:state.scroll.maxTopIndex = [Math]::Max($script:state.scroll.maxTopIndex, $topIndex)
    $script:state.scroll.scrollObserved = $script:state.scroll.maxTopIndex -gt $script:state.scroll.initialTopIndex
    $script:state.scroll.lastRowVisible = $lastVisible
    $script:state.scroll.lastRowTop = $lastRow.Top
    $script:state.scroll.lastRowBottom = $lastRow.Bottom
    $script:state.scroll.viewportHeight = $height
    $atEnd = $lastVisible -and $script:state.scroll.scrollObserved
    $script:confirmEndButton.Visible = $atEnd
    $script:confirmEndButton.Enabled = $atEnd -and -not $script:state.scroll.confirmed
    if (-not $script:state.scroll.confirmed) {
        $script:scrollStatus.Text = if ($atEnd) { 'End of report visible. Choose Confirm end.' } else { 'Scroll down until entry 200 is fully visible.' }
        $script:scrollStatus.AccessibleName = $script:scrollStatus.Text
    }
    if ($changed) { Save-FixtureState 'scroll-position-changed' }
    return $atEnd
}
$script:scrollTimer = [Windows.Forms.Timer]::new()
$script:scrollTimer.Interval = 100
$script:scrollTimer.Add_Tick({ [void](Update-ScrollState) })
$script:confirmEndButton.Add_Click({
    if (-not (Update-ScrollState)) { return }
    $script:state.scroll.confirmed = $true
    $script:state.scroll.confirmCount++
    $script:state.scroll.confirmedTopIndex = $script:state.scroll.topIndex
    $script:state.scroll.confirmedLastRowVisible = $script:state.scroll.lastRowVisible
    $script:confirmEndButton.Enabled = $false
    $script:scrollStatus.Text = 'End acknowledged.'
    $script:scrollStatus.AccessibleName = $script:scrollStatus.Text
    Save-FixtureState 'scroll-end-confirmed'
})

Add-Label $transferTab ('Source reference: ' + $script:state.transfer.sourceReference) 20 24 650
$openTransferButton = Add-Button $transferTab 'Open transfer' 20 82
$script:transferDialog = $null
$openTransferButton.Add_Click({
    if ($null -ne $script:transferDialog -and -not $script:transferDialog.IsDisposed) {
        $script:transferDialog.Activate()
        return
    }
    $script:transferDialog = [Windows.Forms.Form]::new()
    $script:transferDialog.Text = 'Transfer - ' + $script:mainForm.Text
    $script:transferDialog.Name = 'TransferWindow'
    $script:transferDialog.AccessibleName = $script:transferDialog.Text
    $script:transferDialog.Font = $script:mainForm.Font
    $script:transferDialog.ClientSize = [Drawing.Size]::new(690, 235)
    $script:transferDialog.StartPosition = [Windows.Forms.FormStartPosition]::CenterParent
    $script:transferDialog.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
    $script:transferDialog.MinimizeBox = $false
    $script:transferDialog.MaximizeBox = $false
    $script:transferDialog.ShowInTaskbar = $false
    $script:transferDestination = Add-Field $script:transferDialog 'Destination reference' 25
    $script:transferCommit = Add-Button $script:transferDialog 'Commit' 180 90
    $script:transferStatus = [Windows.Forms.Label]::new()
    $script:transferStatus.Name = 'TransferStatus'
    $script:transferStatus.Text = 'Enter the source reference, then Commit.'
    $script:transferStatus.AccessibleName = $script:transferStatus.Text
    $script:transferStatus.Location = [Drawing.Point]::new(20, 155)
    $script:transferStatus.Size = [Drawing.Size]::new(650, 60)
    $script:transferDialog.Controls.Add($script:transferStatus)
    $script:transferCommit.Add_Click({
        $script:state.transfer.destinationReference = $script:transferDestination.Text
        if ($script:transferDestination.Text -cne $script:state.transfer.sourceReference) {
            $script:state.transfer.error = 'Reference does not match the source.'
            $script:transferStatus.Text = $script:state.transfer.error
            $script:transferStatus.AccessibleName = $script:transferStatus.Text
            Save-FixtureState 'transfer-rejected'
            return
        }
        $script:state.transfer.error = $null
        $script:state.transfer.committed = $true
        $script:state.transfer.commitCount++
        $script:transferCommit.Enabled = $false
        $script:transferStatus.Text = 'Transfer saved.'
        $script:transferStatus.AccessibleName = $script:transferStatus.Text
        Save-FixtureState 'transfer-committed'
    })
    $script:transferDialog.Add_FormClosed({
        $script:state.transfer.windowOpen = $false
        Save-FixtureState 'transfer-closed'
    })
    $script:transferDialog.Show($script:mainForm)
    $script:state.transfer.opened = $true
    $script:state.transfer.windowOpen = $true
    $script:state.transfer.windowTitle = $script:transferDialog.Text
    Save-FixtureState 'transfer-opened'
})

# Tree: collapsed nested nodes; only a city can be assigned.
Add-Label $treeTab 'Locations' 20 16 300
$script:locationTree = [Windows.Forms.TreeView]::new()
$script:locationTree.Name = 'Locations'
$script:locationTree.AccessibleName = 'Locations'
$script:locationTree.Location = [Drawing.Point]::new(20, 48)
$script:locationTree.Size = [Drawing.Size]::new(420, 360)
$script:locationTree.PathSeparator = '/'
$script:locationTree.HideSelection = $false
# Checkable rows, as in installer feature trees: selecting a row must not check it.
$script:locationTree.CheckBoxes = $true
$locations = [ordered]@{
    'Americas' = [ordered]@{ 'Canada' = @('Toronto', 'Vancouver'); 'United States' = @('Austin', 'Seattle') }
    'Europe' = [ordered]@{ 'France' = @('Lyon', 'Paris'); 'Germany' = @('Berlin', 'Hamburg', 'Munich'); 'United Kingdom' = @('Bristol', 'London') }
    'Asia Pacific' = [ordered]@{ 'Australia' = @('Melbourne', 'Sydney'); 'Japan' = @('Osaka', 'Tokyo') }
}
foreach ($region in $locations.Keys) {
    $regionNode = $script:locationTree.Nodes.Add($region, $region)
    foreach ($country in $locations[$region].Keys) {
        $countryNode = $regionNode.Nodes.Add($country, $country)
        foreach ($city in $locations[$region][$country]) { [void]$countryNode.Nodes.Add($city, $city) }
    }
}
$treeTab.Controls.Add($script:locationTree)
$assignLocationButton = Add-Button $treeTab 'Assign location' 460 48 200
$script:treeStatus = Add-Status $treeTab 'TreeStatus' 'No location assigned.' 420
$script:locationTree.Add_AfterSelect({
    $script:state.tree.selectedPath = $_.Node.FullPath
    Save-FixtureState 'tree-selection'
})
$script:locationTree.Add_AfterExpand({
    $script:state.tree.expandCount++
    Save-FixtureState 'tree-expanded'
})
$script:locationTree.Add_AfterCheck({
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($path in ($script:state.tree.checkedList -split ',')) { if ($path) { $paths.Add($path) } }
    if ($_.Node.Checked) { if (-not $paths.Contains($_.Node.FullPath)) { $paths.Add($_.Node.FullPath) } }
    else { [void]$paths.Remove($_.Node.FullPath) }
    $script:state.tree.checkedList = $paths -join ','
    $script:state.tree.checkCount++
    Save-FixtureState 'tree-checked'
})
$assignLocationButton.Add_Click({
    $node = $script:locationTree.SelectedNode
    if ($null -eq $node -or $node.Nodes.Count -gt 0) {
        Set-Status $script:treeStatus 'Select a city, not a region or country.'
        return
    }
    $script:state.tree.assignedPath = $node.FullPath
    $script:state.tree.assignCount++
    Save-FixtureState 'tree-assigned'
    Set-Status $script:treeStatus ('Assigned: ' + $node.FullPath)
})

# Grid: 60 invoices, so the target row starts offscreen; only Status is editable.
Add-Label $gridTab 'Invoices' 20 16 300
$script:invoiceGrid = [Windows.Forms.DataGridView]::new()
$script:invoiceGrid.Name = 'Invoices'
$script:invoiceGrid.AccessibleName = 'Invoices'
$script:invoiceGrid.Location = [Drawing.Point]::new(20, 48)
$script:invoiceGrid.Size = [Drawing.Size]::new(680, 350)
$script:invoiceGrid.AllowUserToAddRows = $false
$script:invoiceGrid.AllowUserToDeleteRows = $false
$script:invoiceGrid.RowHeadersVisible = $false
$script:invoiceGrid.AutoSizeColumnsMode = [Windows.Forms.DataGridViewAutoSizeColumnsMode]::Fill
foreach ($column in @('Invoice', 'Customer', 'Amount', 'Status')) {
    $index = $script:invoiceGrid.Columns.Add($column, $column)
    $script:invoiceGrid.Columns[$index].ReadOnly = $column -ne 'Status'
    $script:invoiceGrid.Columns[$index].SortMode = [Windows.Forms.DataGridViewColumnSortMode]::NotSortable
}
$customers = @('Northwind Traders', 'Fabrikam Retail', 'Adventure Works', 'Tailspin Toys', 'Wide World Importers', 'Litware Labs', 'Proseware', 'Coho Winery')
$script:originalStatuses = @{}
foreach ($number in 1001..1060) {
    $invoice = 'INV-' + $number
    $customer = $customers[$number % $customers.Count]
    $amount = (100 + ($number * 37) % 900 + (($number * 13) % 100) / 100.0).ToString('N2', [Globalization.CultureInfo]::InvariantCulture)
    $status = if ($number % 5 -eq 0) { 'Paid' } else { 'Open' }
    if ($number -eq 1042) { $customer = $script:state.grid.targetCustomer; $amount = $script:state.grid.targetAmount; $status = 'Open' }
    [void]$script:invoiceGrid.Rows.Add($invoice, $customer, $amount, $status)
    $script:originalStatuses[$invoice] = $status
}
$gridTab.Controls.Add($script:invoiceGrid)
$saveGridButton = Add-Button $gridTab 'Save changes' 20 410
$script:gridStatus = Add-Status $gridTab 'GridStatus' 'No changes saved.' 456
$saveGridButton.Add_Click({
    [void]$script:invoiceGrid.EndEdit()
    $changed = @(foreach ($row in $script:invoiceGrid.Rows) {
        $invoice = [string]$row.Cells['Invoice'].Value
        if ([string]$row.Cells['Status'].Value -cne $script:originalStatuses[$invoice]) { $invoice }
    })
    $target = @($script:invoiceGrid.Rows | Where-Object { $_.Cells['Invoice'].Value -eq 'INV-1042' })[0]
    $script:state.grid.saveCount++
    $script:state.grid.changedList = $changed -join ','
    $script:state.grid.targetStatus = [string]$target.Cells['Status'].Value
    Save-FixtureState 'grid-saved'
    Set-Status $script:gridStatus ('Saved ' + $changed.Count + ' changed invoice(s).')
})

# Setup: a modal three-page wizard with radio buttons, a spinner, a slider and gated navigation.
Add-Label $setupTab 'Configure a demo workspace with the setup wizard.' 20 20 650
$startSetupButton = Add-Button $setupTab 'Start setup' 20 62
$script:setupStatus = Add-Status $setupTab 'SetupStatus' 'Setup has not been completed.' 118

function Get-WizardPlan { @($script:wizard.plans.Keys | Where-Object { $script:wizard.plans[$_].Checked })[0] }

function Update-Wizard {
    $page = $script:wizard.page
    for ($i = 0; $i -lt $script:wizard.pages.Count; $i++) { $script:wizard.pages[$i].Visible = $i -eq $page }
    $script:wizard.back.Enabled = $page -gt 0
    $script:wizard.next.Visible = $page -lt 2
    $script:wizard.next.Enabled = $page -ne 1 -or $script:wizard.terms.Checked
    $script:wizard.finish.Visible = $page -eq 2
    Set-Status $script:wizard.summary ('Plan: ' + (Get-WizardPlan) + '. Seats: ' + $script:wizard.seats.Value +
        '. Storage: ' + $script:wizard.storage.Value + ' GB.')
}

$startSetupButton.Add_Click({
    $form = [Windows.Forms.Form]::new()
    $form.Text = 'Setup wizard - ' + $script:mainForm.Text
    $form.Name = 'SetupWizard'
    $form.AccessibleName = $form.Text
    $form.Font = $script:mainForm.Font
    $form.ClientSize = [Drawing.Size]::new(640, 400)
    $form.StartPosition = [Windows.Forms.FormStartPosition]::CenterParent
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
    $form.MinimizeBox = $false
    $form.MaximizeBox = $false
    $form.ShowInTaskbar = $false
    $script:wizard = @{ form = $form; page = 0; pages = @(); plans = [ordered]@{} }
    foreach ($pageName in @('Choose a plan', 'Capacity', 'Summary')) {
        $panel = [Windows.Forms.Panel]::new()
        $panel.Name = ($pageName -replace '[^A-Za-z0-9]', '') + 'Page'
        $panel.AccessibleName = $pageName
        $panel.Location = [Drawing.Point]::new(0, 0)
        $panel.Size = [Drawing.Size]::new(640, 330)
        Add-Label $panel $pageName 20 16 600
        $form.Controls.Add($panel)
        $script:wizard.pages += $panel
    }
    $y = 60
    foreach ($plan in @('Basic', 'Pro', 'Enterprise')) {
        $radio = [Windows.Forms.RadioButton]::new()
        $radio.Text = $plan
        $radio.Name = 'Plan' + $plan
        $radio.AccessibleName = $plan
        $radio.Location = [Drawing.Point]::new(40, $y)
        $radio.Size = [Drawing.Size]::new(300, 30)
        $radio.Checked = $plan -eq 'Basic'
        $script:wizard.pages[0].Controls.Add($radio)
        $script:wizard.plans[$plan] = $radio
        $y += 40
    }
    Add-Label $script:wizard.pages[1] 'Seats' 20 64
    $seats = [Windows.Forms.NumericUpDown]::new()
    $seats.Name = 'Seats'
    $seats.AccessibleName = 'Seats'
    $seats.Minimum = 1
    $seats.Maximum = 500
    $seats.Value = 5
    $seats.Location = [Drawing.Point]::new(200, 60)
    $seats.Size = [Drawing.Size]::new(120, 28)
    $script:wizard.pages[1].Controls.Add($seats)
    $script:wizard.seats = $seats
    Add-Label $script:wizard.pages[1] 'Storage (GB)' 20 114
    $storage = [Windows.Forms.TrackBar]::new()
    $storage.Name = 'Storage'
    $storage.AccessibleName = 'Storage (GB)'
    $storage.Minimum = 0
    $storage.Maximum = 1000
    $storage.SmallChange = 50
    $storage.LargeChange = 100
    $storage.TickFrequency = 100
    $storage.Value = 100
    $storage.Location = [Drawing.Point]::new(200, 106)
    $storage.Size = [Drawing.Size]::new(400, 45)
    $script:wizard.pages[1].Controls.Add($storage)
    $script:wizard.storage = $storage
    $script:wizard.storageLabel = Add-Status $script:wizard.pages[1] 'StorageValue' 'Storage: 100 GB' 160 30
    $storage.Add_ValueChanged({ Set-Status $script:wizard.storageLabel ('Storage: ' + $script:wizard.storage.Value + ' GB') })
    $script:wizard.terms = Add-CheckBox $script:wizard.pages[1] 'I accept the terms' 20 210
    $script:wizard.summary = Add-Status $script:wizard.pages[2] 'SetupSummary' '' 60 120
    $script:wizard.back = Add-Button $form 'Back' 160 345 100
    $script:wizard.next = Add-Button $form 'Next' 270 345 100
    $script:wizard.finish = Add-Button $form 'Finish' 380 345 100
    $cancel = Add-Button $form 'Cancel' 490 345 100
    $cancel.DialogResult = [Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancel
    $script:wizard.terms.Add_CheckedChanged({ Update-Wizard })
    $script:wizard.back.Add_Click({ $script:wizard.page--; Update-Wizard })
    $script:wizard.next.Add_Click({ $script:wizard.page++; Update-Wizard })
    $script:wizard.finish.Add_Click({
        $script:state.wizard.completed = $true
        $script:state.wizard.finishCount++
        $script:state.wizard.plan = Get-WizardPlan
        $script:state.wizard.seats = [int]$script:wizard.seats.Value
        $script:state.wizard.storageGb = [int]$script:wizard.storage.Value
        $script:state.wizard.acceptedTerms = $script:wizard.terms.Checked
        Save-FixtureState 'wizard-finished'
        Set-Status $script:setupStatus 'Setup completed.'
        $script:wizard.form.DialogResult = [Windows.Forms.DialogResult]::OK
    })
    Update-Wizard
    $script:state.wizard.opened = $true
    Save-FixtureState 'wizard-opened'
    try {
        if ($form.ShowDialog($script:mainForm) -ne [Windows.Forms.DialogResult]::OK) {
            $script:state.wizard.cancelled = $true
            Save-FixtureState 'wizard-cancelled'
        }
    }
    finally { $form.Dispose() }
})
# Account: the first submission is rejected by a message box that suggests an alternative.
$script:accountFields = @{}
$row = 20
foreach ($label in @('Username', 'Email', 'Age')) {
    $script:accountFields[$label] = Add-Field $accountTab $label $row
    $row += 39
}
$createAccountButton = Add-Button $accountTab 'Create account' 180 ($row + 8) 185
$script:accountStatus = Add-Status $accountTab 'AccountStatus' 'No account created.' ($row + 60)
$createAccountButton.Add_Click({
    $username = $script:accountFields['Username'].Text.Trim()
    $email = $script:accountFields['Email'].Text.Trim()
    $age = 0
    if (-not $username -or $email -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$' -or
        -not [int]::TryParse($script:accountFields['Age'].Text, [ref]$age) -or $age -lt 13 -or $age -gt 120) {
        $script:state.account.invalidCount++
        Save-FixtureState 'account-invalid'
        Set-Status $script:accountStatus 'Enter a username, a valid email and an age from 13 to 120.'
        return
    }
    if ($username -ceq 'morgan_r') {
        $script:state.account.rejectedCount++
        Save-FixtureState 'account-rejected'
        Set-Status $script:accountStatus 'Username unavailable.'
        [void][Windows.Forms.MessageBox]::Show($script:mainForm,
            "The username 'morgan_r' is already taken. Suggested alternative: morgan_r7",
            'Username unavailable - ' + $script:mainForm.Text,
            [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Warning)
        return
    }
    $script:state.account.created = $true
    $script:state.account.createdCount++
    $script:state.account.username = $username
    $script:state.account.email = $email
    $script:state.account.age = $age
    Save-FixtureState 'account-created'
    Set-Status $script:accountStatus ('Account created for ' + $username + '.')
})

# Files: a details list whose actions live in a right-click context menu with confirmation.
Add-Label $filesTab 'Files' 20 16 300
$script:fileList = [Windows.Forms.ListView]::new()
$script:fileList.Name = 'Files'
$script:fileList.AccessibleName = 'Files'
$script:fileList.View = [Windows.Forms.View]::Details
$script:fileList.FullRowSelect = $true
$script:fileList.MultiSelect = $false
$script:fileList.HideSelection = $false
$script:fileList.Location = [Drawing.Point]::new(20, 48)
$script:fileList.Size = [Drawing.Size]::new(680, 350)
foreach ($column in @(@('Name', 250), @('Size', 100), @('Modified', 170), @('Status', 130))) {
    [void]$script:fileList.Columns.Add($column[0], $column[1])
}
$fileRows = @(
    @('budget-2026.xlsx', '48 KB', '2026-01-12'), @('customer-list.csv', '12 KB', '2026-02-03'),
    @('meeting-notes.docx', '31 KB', '2026-03-18'), @('product-roadmap.pptx', '2.4 MB', '2026-04-01'),
    @('report-q1.xlsx', '66 KB', '2026-04-10'), @('report-q2.xlsx', '71 KB', '2026-07-09'),
    @('report-q3.xlsx', '69 KB', '2026-09-30'), @('team-photo.png', '3.1 MB', '2026-05-22'),
    @('travel-policy.pdf', '420 KB', '2026-06-14'), @('vendor-contracts.zip', '8.8 MB', '2026-08-27'))
foreach ($file in $fileRows) {
    $item = [Windows.Forms.ListViewItem]::new([string[]]@($file[0], $file[1], $file[2], 'Active'))
    $item.Name = $file[0]
    [void]$script:fileList.Items.Add($item)
}
$filesTab.Controls.Add($script:fileList)
$script:filesStatus = Add-Status $filesTab 'FilesStatus' 'Right-click a file for actions.' 410
$fileMenu = [Windows.Forms.ContextMenuStrip]::new()
$fileMenu.Name = 'FileActions'
$openFileItem = $fileMenu.Items.Add('Open')
$renameFileItem = $fileMenu.Items.Add('Rename')
$renameFileItem.Enabled = $false
$archiveFileItem = $fileMenu.Items.Add('Archive')
$deleteFileItem = $fileMenu.Items.Add('Delete')
$script:fileList.ContextMenuStrip = $fileMenu

function Get-SelectedFile {
    if ($script:fileList.SelectedItems.Count -ne 1) {
        Set-Status $script:filesStatus 'Select exactly one file first.'
        return $null
    }
    return $script:fileList.SelectedItems[0]
}

function Confirm-FileAction {
    param([string]$Question, [string]$Caption)
    $answer = [Windows.Forms.MessageBox]::Show($script:mainForm, $Question, $Caption + ' - ' + $script:mainForm.Text,
        [Windows.Forms.MessageBoxButtons]::YesNo, [Windows.Forms.MessageBoxIcon]::Question)
    if ($answer -ne [Windows.Forms.DialogResult]::Yes) {
        $script:state.files.declinedCount++
        Save-FixtureState 'file-action-declined'
        return $false
    }
    return $true
}

function Open-SelectedFile {
    $item = Get-SelectedFile
    if ($null -eq $item) { return }
    $script:state.files.openCount++
    $script:state.files.openedList = (@($script:state.files.openedList -split ',' | Where-Object { $_ }) + $item.Text) -join ','
    Save-FixtureState 'file-opened'
    Set-Status $script:filesStatus ('Opened ' + $item.Text + '.')
}
$openFileItem.Add_Click({ Open-SelectedFile })
$script:fileList.Add_ItemActivate({ Open-SelectedFile })
$archiveFileItem.Add_Click({
    $item = Get-SelectedFile
    if ($null -eq $item -or -not (Confirm-FileAction ('Archive ' + $item.Text + '?') 'Archive file')) { return }
    $item.SubItems[3].Text = 'Archived'
    $script:state.files.archiveCount++
    $script:state.files.archivedList = (@($script:state.files.archivedList -split ',' | Where-Object { $_ }) + $item.Text) -join ','
    Save-FixtureState 'file-archived'
    Set-Status $script:filesStatus ('Archived ' + $item.Text + '.')
})
$deleteFileItem.Add_Click({
    $item = Get-SelectedFile
    if ($null -eq $item -or -not (Confirm-FileAction ('Delete ' + $item.Text + ' permanently?') 'Delete file')) { return }
    $script:fileList.Items.Remove($item)
    $script:state.files.deleteCount++
    $script:state.files.deletedList = (@($script:state.files.deletedList -split ',' | Where-Object { $_ }) + $item.Text) -join ','
    Save-FixtureState 'file-deleted'
    Set-Status $script:filesStatus ('Deleted ' + $item.Text + '.')
})

$script:mainForm.Add_Shown({
    $script:state.ready = $true
    Save-FixtureState 'ready'
    [void]$script:orderFields['First name'].Focus()
    $script:scrollTimer.Start()
})
$script:mainForm.Add_FormClosed({
    $script:state.ready = $false
    $script:state.closed = $true
    Save-FixtureState 'closed'
})
try { [Windows.Forms.Application]::Run($script:mainForm) }
finally {
    $script:reportTimer.Dispose()
    $script:scrollTimer.Dispose()
    $script:mainForm.Dispose()
}
