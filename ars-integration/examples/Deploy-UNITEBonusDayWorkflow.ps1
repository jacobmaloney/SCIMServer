# =============================================================================
# Deploy-UNITEBonusDayWorkflow - creates/updates:
#   1. ScriptModule  : UNITE-BonusDay (loads UNITE-BonusDay.ps1)
#   2. Workflow      : UNITE Examples - Bonus Day Off
#
# Prerequisites you must do in MMC FIRST (see SETUP.md):
#   * Create virtual attributes edsva-BonusDayRequest (string)
#     and edsva-BonusDaysGranted (integer) on the user class.
#   * Create the Web Interface command button (Ex 4).
#
# Re-runnable - finds existing rows by name, updates them in place.
# =============================================================================

param(
    [string]$Server = "192.168.1.30",
    [string]$Database = "ActiveRoles820",
    [string]$User = "sa",
    [string]$Password = "ITsupp0rt!",
    [string]$WorkflowName  = "UNITE Examples - Bonus Day Off",
    [string]$ScriptModuleName = "UNITE-BonusDay",
    # Parent containers in the AR Configuration tree. Both are CN=UNITE-2026
    # but under DIFFERENT roots (CN=Script Modules vs CN=Workflow,CN=Policies).
    # These GUIDs are taken from the existing UNITE-SCIMRest + UNITE Provisioning
    # Hub rows respectively. Override if your tenant placed them elsewhere.
    [string]$ScriptModuleParentGuid = "b184d443-23d5-4c99-b0d0-8cdc4ec1da37",  # CN=UNITE-2026,CN=Script Modules,CN=Configuration
    [string]$WorkflowParentGuid     = "32e903fc-097e-49d3-9a7c-1f3eeedf3e4d",  # CN=UNITE-2026,CN=Workflow,CN=Policies,CN=Configuration
    # AD scope container - the OU whose users this workflow watches. Same as
    # the SCIM Provisioning Hub uses. NOT the Workflows-table parent (above).
    [string]$AdScopeContainerGuid   = "80a8172f-2d21-4035-b5d0-2675f24b66a1"
)

$ErrorActionPreference = "Stop"
$cs = "Server=$Server;Database=$Database;User Id=$User;Password=$Password;TrustServerCertificate=true;"

# -----------------------------------------------------------------------------
# 1. ScriptModule deploy - upsert by name. The script body is the contents of
#    UNITE-BonusDay.ps1 (no mapping table to concatenate - the demo has no
#    per-app variants).
# -----------------------------------------------------------------------------

$scriptPath = Join-Path $PSScriptRoot "UNITE-BonusDay.ps1"
if (-not (Test-Path $scriptPath)) { Write-Error "Script file not found: $scriptPath"; exit 1 }
$scriptText = [System.IO.File]::ReadAllText($scriptPath)

# Parse-check before we ship
$tokens = $errors = $null
[System.Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$tokens, [ref]$errors) | Out-Null
if ($errors.Count -ne 0) {
    $errors | ForEach-Object { Write-Host "PARSE ERR line $($_.Extent.StartLineNumber): $($_.Message)" }
    exit 1
}

$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT CAST(objectGUID AS UNIQUEIDENTIFIER) FROM ScriptModules WHERE name = @n"
[void]$cmd.Parameters.AddWithValue("@n", $ScriptModuleName)
$existingGuid = $cmd.ExecuteScalar()

if ($existingGuid) {
    $scriptModuleGuid = [Guid]$existingGuid
    Write-Host "ScriptModule '$ScriptModuleName' exists (GUID $scriptModuleGuid); updating body."
    $u = $conn.CreateCommand()
    $u.CommandText = "UPDATE ScriptModules SET edsaScriptText = @t, whenChanged = SYSDATETIME() WHERE objectGUID = @g"
    $pt = $u.Parameters.Add("@t", [System.Data.SqlDbType]::NVarChar, -1); $pt.Value = $scriptText
    [void]$u.Parameters.AddWithValue("@g", $scriptModuleGuid)
    [void]$u.ExecuteNonQuery()
} else {
    $scriptModuleGuid = [Guid]::NewGuid()
    Write-Host "Creating ScriptModule '$ScriptModuleName' (GUID $scriptModuleGuid)."
    $dn = "CN=$ScriptModuleName,CN=UNITE-2026,CN=Script Modules,CN=Configuration"
    $i = $conn.CreateCommand()
    # 'cn' is a computed column - excluded from INSERT (SQL generates it from 'name').
    # 'cn' is a computed column - excluded from INSERT (SQL generates it from 'name').
    # edsaScriptType is INT (enum); 0 matches the existing UNITE-SCIMRest row.
    $i.CommandText = @"
INSERT INTO ScriptModules
    (objectGUID, ParentObjectGUID, name, distinguishedName, objectClass,
     edsaScriptText, edsaScriptLanguage, edsaScriptType,
     whenCreated, whenChanged, edsaIsPredefined, edsaSystemObject)
VALUES (@g, @p, @n, @d, 'edsScriptModule', @t, 'PowerShell', 0,
        SYSDATETIME(), SYSDATETIME(), 0, 0);
"@
    [void]$i.Parameters.AddWithValue("@g", $scriptModuleGuid)
    [void]$i.Parameters.AddWithValue("@p", [Guid]$ScriptModuleParentGuid)
    [void]$i.Parameters.AddWithValue("@n", $ScriptModuleName)
    [void]$i.Parameters.AddWithValue("@d", $dn)
    $pt = $i.Parameters.Add("@t", [System.Data.SqlDbType]::NVarChar, -1); $pt.Value = $scriptText
    [void]$i.ExecuteNonQuery()
}

# -----------------------------------------------------------------------------
# 2. Workflow XAML - build inner XAML at "Layer 1" form (entity-encoded for
#    attribute embedding), then escape once more for <Xaml> text content.
#    LESSONS FROM SCIM HUB:
#      * Outer escape does NOT touch '"' (only attribute values do).
#      * Source for embedded XML uses '&lt;' / '&gt;' / '&quot;' DIRECTLY -
#        don't put '<' '>' '"' in source then escape, you'll double-encode
#        ampersand-entities like '&#xD;'.
#      * Activity names with '?' need ${var}? (PS interp).
# -----------------------------------------------------------------------------

# Helper: build an AddRecordToReportActivity. Header/Message must NOT contain
# '<' '>' '&' '"' '\'' or they will break inner XML.
function Add-Report {
    param([string]$Header, [string]$Message, [string]$ActivityName, [string]$XName)
    $defL1 = "&lt;?xml version=&quot;1.0&quot; encoding=&quot;utf-16&quot;?&gt;&lt;AddRecordToReportActivityDefinition xmlns:xsd=&quot;http://www.w3.org/2001/XMLSchema&quot; xmlns:xsi=&quot;http://www.w3.org/2001/XMLSchema-instance&quot; IsErrorType=&quot;false&quot; xmlns=&quot;urn:schemas-quest-com:ActiveRolesServer&quot;&gt;&lt;Header&gt;&lt;ArsToken xsi:type=&quot;TextToken&quot; TextTokenType=&quot;Default&quot;&gt;&lt;Text&gt;$Header&lt;/Text&gt;&lt;/ArsToken&gt;&lt;/Header&gt;&lt;Message&gt;&lt;ArsToken xsi:type=&quot;TextToken&quot; TextTokenType=&quot;Default&quot;&gt;&lt;Text&gt;$Message&lt;/Text&gt;&lt;/ArsToken&gt;&lt;/Message&gt;&lt;/AddRecordToReportActivityDefinition&gt;"
    return "<ns0:AddRecordToReportActivity SuppressError=`"False`" ActivityDefinitionXML=`"$defL1`" ActivityName=`"$ActivityName`" x:Name=`"$XName`" />"
}

# Helper: build a PowerShellActivity calling a function in our script module.
function PS-Activity {
    param([string]$FunctionToRun, [string]$ActivityName, [string]$XName, [string]$Guid, [bool]$Suppress = $true)
    $paramsL1 = "&lt;?xml version=&quot;1.0&quot; encoding=&quot;utf-16&quot;?&gt;&lt;CustomActivityParameter xmlns:xsd=&quot;http://www.w3.org/2001/XMLSchema&quot; xmlns:xsi=&quot;http://www.w3.org/2001/XMLSchema-instance&quot; xmlns=&quot;urn:schemas-quest-com:ActiveRolesServer&quot; /&gt;"
    $suppressStr = if ($Suppress) { "True" } else { "False" }
    return "<ns0:PowerShellActivity SuppressError=`"$suppressStr`" PolicyTypeID=`"{x:Null}`" NotificationConfigurationXml=`"{x:Null}`" ScriptModuleGuid=`"$Guid`" Parameters=`"$paramsL1`" FunctionToRun=`"$FunctionToRun`" ActivityName=`"$ActivityName`" FunctionToDeclareParameters=`"{x:Null}`" x:Name=`"$XName`" />"
}

# Helper: IfElse condition comparing WorkflowTargetToken[attr] vs literal int.
# Operator is XML-attribute value; common ones are "==", "!=", "<", "<=", ">", ">=".
function Condition-TargetLessThanLiteral {
    param([string]$AttrName, [int]$LiteralInt)
    return "&lt;AdvancedConditionOperationFilter xmlns:xsd=&quot;http://www.w3.org/2001/XMLSchema&quot; xmlns:xsi=&quot;http://www.w3.org/2001/XMLSchema-instance&quot; policyCheckEnabled=&quot;false&quot;&gt;&lt;And&gt;&lt;TokenCondition operator=&quot;&amp;lt;&quot;&gt;&lt;LeftOperand&gt;&lt;ArsToken xmlns:q1=&quot;urn:schemas-quest-com:ActiveRolesServer&quot; xsi:type=&quot;q1:WorkflowTargetToken&quot; isObject=&quot;false&quot;&gt;&lt;q1:Property name=&quot;$AttrName&quot; charNumber=&quot;0&quot; limitValueString=&quot;false&quot; limitValueCount=&quot;0&quot; adjustCase=&quot;false&quot; makeCaseLower=&quot;false&quot; excludeCharacters=&quot;false&quot; excludeSpace=&quot;false&quot; /&gt;&lt;/ArsToken&gt;&lt;/LeftOperand&gt;&lt;RightOperand&gt;&lt;ArsToken xmlns:q2=&quot;urn:schemas-quest-com:ActiveRolesServer&quot; xsi:type=&quot;q2:TextToken&quot; TextTokenType=&quot;Default&quot;&gt;&lt;q2:Text&gt;$LiteralInt&lt;/q2:Text&gt;&lt;/ArsToken&gt;&lt;/RightOperand&gt;&lt;/TokenCondition&gt;&lt;/And&gt;&lt;/AdvancedConditionOperationFilter&gt;"
}

# Helper: ApprovalActivity. ApproverDN comes from WorkflowTargetToken[manager].
function Approval-Activity {
    param([string]$ActivityName, [string]$XName)
    # Approval activities reference a "manager-of-target" approver via the
    # built-in PrimaryOwnerToken (the target's manager attribute, by
    # convention in ARS approval policies). The XAML below mirrors the
    # built-in "Approval by Primary Owner (Manager)" workflow's shape.
    $defL1 = "&lt;?xml version=&quot;1.0&quot; encoding=&quot;utf-16&quot;?&gt;&lt;ApprovalActivityDefinition xmlns:xsd=&quot;http://www.w3.org/2001/XMLSchema&quot; xmlns:xsi=&quot;http://www.w3.org/2001/XMLSchema-instance&quot; xmlns=&quot;urn:schemas-quest-com:ActiveRolesServer&quot;&gt;&lt;ApproversList&gt;&lt;ArsToken xsi:type=&quot;PrimaryOwnerToken&quot;&gt;&lt;ApproverType&gt;Manager&lt;/ApproverType&gt;&lt;/ArsToken&gt;&lt;/ApproversList&gt;&lt;EscalationApprovers /&gt;&lt;/ApprovalActivityDefinition&gt;"
    return "<ns0:ApprovalActivity SuppressError=`"False`" ActivityDefinitionXML=`"$defL1`" ActivityName=`"$ActivityName`" x:Name=`"$XName`" />"
}

$smGuid = $scriptModuleGuid.ToString()

$rptStart = Add-Report `
    -Header "Ex 5a: AddRecordToReport - request received" `
    -Message "Web Interface button set edsva-BonusDayRequest. Workflow firing now. See activities below for each example." `
    -ActivityName "Ex 5a: AddRecordToReport" -XName "rptStart"

$ex2 = PS-Activity -FunctionToRun "Ex2-ReadContext" `
    -ActivityName "Ex 2: PS read workflow parameter + AD attributes" -XName "psEx2" `
    -Guid $smGuid -Suppress $true

$ex8 = Approval-Activity -ActivityName "Ex 8: Approval - manager approves" -XName "appEx8"

$ex7 = PS-Activity -FunctionToRun "Ex7-GrantBonusDay" `
    -ActivityName "Ex 7: PS Set-QADObject grant" -XName "psEx7" `
    -Guid $smGuid -Suppress $true

$ex3 = PS-Activity -FunctionToRun "Ex3-PublishResult" `
    -ActivityName "Ex 3: PS return new value to next step" -XName "psEx3" `
    -Guid $smGuid -Suppress $true

$rptOK = Add-Report `
    -Header "Ex 5b: AddRecordToReport - granted" `
    -Message "Bonus day granted. New balance visible in the Ex 3 row above. Reason captured in Ex 2 row." `
    -ActivityName "Ex 5b: AddRecordToReport (granted)" -XName "rptOK"

$rptDenied = Add-Report `
    -Header "Ex 5c: AddRecordToReport - denied (at cap)" `
    -Message "User is already at the BonusDayCap (default 5). No change. Reset edsva-BonusDaysGranted in MMC to test again." `
    -ActivityName "Ex 5c: AddRecordToReport (denied)" -XName "rptDenied"

$cleanup = PS-Activity -FunctionToRun "Ex9-ClearRequestMarker" `
    -ActivityName "cleanup: clear edsva-BonusDayRequest" -XName "psCleanup" `
    -Guid $smGuid -Suppress $true

# Ex 6: IfElse with two branches. Then-branch: under cap (5) -> Ex 7 + Ex 3 + Ex 5b.
# Else-branch: at cap -> Ex 5c. (Default IfElseBranchActivity has no ConditionXml.)
$capCond = Condition-TargetLessThanLiteral -AttrName "edsva-BonusDaysGranted" -LiteralInt 5

$ifElse = @"
<ns0:IfElseActivity SuppressError="False" ExecutionContext="{x:Null}" ActivityName="Ex 6: IfElse - under cap?" x:Name="ifCap">
  <ns0:IfElseBranchActivity SuppressError="False" ExecutionContext="{x:Null}" ConditionXml="$capCond" ActivityName="Under cap" x:Name="ifCapUnder">
    $ex7
    $ex3
    $rptOK
  </ns0:IfElseBranchActivity>
  <ns0:IfElseBranchActivity SuppressError="False" ExecutionContext="{x:Null}" ConditionXml="" ActivityName="At cap (default)" x:Name="ifCapAt">
    $rptDenied
  </ns0:IfElseBranchActivity>
</ns0:IfElseActivity>
"@

# Compose the full inner XAML
$xamlInner = @"
<?xml version="1.0" encoding="utf-16"?><ns0:ARSWorkflowActivity SuppressError="False" Description="ActiveRoles Workflow Activity" ExecutionContext="{p1:Null}" ActivityName="{p1:Null}" x:Name="Activity" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:p1="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:ns0="clr-namespace:ActiveRoles.Workflow.Activities;Assembly=ActiveRoles.Workflow.Activities, Version=8.2.1.0, Culture=neutral, PublicKeyToken=37ba620bec38a887">
<ns0:ServiceExecutionActivity SuppressError="False" x:Name="serviceExecutionActivity1" ActivityName="{x:Null}" />
$rptStart
$ex2
$ex8
$ifElse
$cleanup
</ns0:ARSWorkflowActivity>
"@

# Escape inner XAML for <Xaml> text content. DON'T touch '"' (only attribute values do).
$xamlEsc = $xamlInner.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

# -----------------------------------------------------------------------------
# 3. Workflow definition (the ArsWorkflow wrapper around <Xaml>).
#    Workflow Parameters block declares 'BonusDayCap' so Ex 2's
#    $Workflow.Parameter("BonusDayCap") returns a value. Default 5.
# -----------------------------------------------------------------------------

$workflowGuid = [Guid]::NewGuid()
$workflowDef = @"
<ArsWorkflow xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" guid="$workflowGuid">
  <Xaml>$xamlEsc</Xaml>
  <InitializationScript />
  <Conditions>
    <Operation xsi:type="ModifyObject" policyCheckEnabled="false" objectClass="user">
      <AttributeNames>
        <string>edsva-BonusDayRequest</string>
      </AttributeNames>
    </Operation>
    <InitiatorsAndScopes>
      <InitiatorAndScopeFilter>
        <Initiator xsi:type="SecurityIdentifierInitiatorFilter" sid="S-1-1-0" />
        <Scope xsi:type="IncludeContainer">
          <Container guid="$AdScopeContainerGuid" />
        </Scope>
      </InitiatorAndScopeFilter>
    </InitiatorsAndScopes>
    <AdvancedConditions policyCheckEnabled="false">
      <And />
    </AdvancedConditions>
  </Conditions>
  <Parameters>
    <Parameter Name="BonusDayCap" Type="System.String">
      <DefaultValue>5</DefaultValue>
      <DisplayName>Bonus Day Cap (max per user)</DisplayName>
      <Description>Demo of Example 2: this value is read by Ex2-ReadContext via Workflow.Parameter('BonusDayCap'). Defaults to 5.</Description>
    </Parameter>
  </Parameters>
  <Settings>
    <AccountType>ServiceAccount</AccountType>
    <EnforceApproval>false</EnforceApproval>
  </Settings>
</ArsWorkflow>
"@

# -----------------------------------------------------------------------------
# 4. Upsert the Workflows row
# -----------------------------------------------------------------------------

$cmd2 = $conn.CreateCommand()
$cmd2.CommandText = "SELECT CAST(objectGUID AS UNIQUEIDENTIFIER) FROM Workflows WHERE name = @n"
[void]$cmd2.Parameters.AddWithValue("@n", $WorkflowName)
$existingWf = $cmd2.ExecuteScalar()

if ($existingWf) {
    Write-Host "Workflow '$WorkflowName' exists (GUID $existingWf); updating definition only."
    $upd = $conn.CreateCommand()
    $upd.CommandText = "UPDATE Workflows SET edsaWorkflowDefinition = @d, whenChanged = SYSDATETIME() WHERE objectGUID = @g"
    $pd = $upd.Parameters.Add("@d", [System.Data.SqlDbType]::NVarChar, -1); $pd.Value = $workflowDef
    [void]$upd.Parameters.AddWithValue("@g", $existingWf)
    [void]$upd.ExecuteNonQuery()
} else {
    Write-Host "Creating Workflow '$WorkflowName' (GUID $workflowGuid)."
    # DN format matches existing UNITE Provisioning Hub: under CN=Workflow,CN=Policies
    $dn = "CN=$WorkflowName,CN=UNITE-2026,CN=Workflow,CN=Policies,CN=Configuration"
    $ins = $conn.CreateCommand()
    # 'cn' is a computed column - excluded from INSERT.
    $ins.CommandText = @"
INSERT INTO Workflows
    (objectGUID, ParentObjectGUID, name, distinguishedName, objectClass,
     edsaWorkflowDefinition, whenCreated, whenChanged, edsaIsPredefined, edsaSystemObject,
     edsaWorkflowIsDisabled)
VALUES (@g, @p, @n, @d, 'edsWorkflow', @def, SYSDATETIME(), SYSDATETIME(), 0, 0, 0);
"@
    [void]$ins.Parameters.AddWithValue("@g", $workflowGuid)
    [void]$ins.Parameters.AddWithValue("@p", [Guid]$WorkflowParentGuid)
    [void]$ins.Parameters.AddWithValue("@n", $WorkflowName)
    [void]$ins.Parameters.AddWithValue("@d", $dn)
    $pdef = $ins.Parameters.Add("@def", [System.Data.SqlDbType]::NVarChar, -1); $pdef.Value = $workflowDef
    [void]$ins.ExecuteNonQuery()
}

$conn.Close()

Write-Host ""
Write-Host "=== Done ==="
Write-Host "ScriptModule: $ScriptModuleName ($scriptModuleGuid)"
Write-Host "Workflow:     $WorkflowName"
Write-Host ""
Write-Host "Next steps (see SETUP.md):"
Write-Host "  1. Verify virtual attributes edsva-BonusDayRequest and"
Write-Host "     edsva-BonusDaysGranted exist on the user class."
Write-Host "  2. Add the Web Interface 'Grant Bonus Day' command button."
Write-Host "  3. Test by setting edsva-BonusDayRequest on a user under the UNITE-2026 OU."
