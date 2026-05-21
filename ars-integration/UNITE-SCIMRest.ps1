# ============================================================================
# UNITE-SCIMRest - Universal SCIM 2.0 provisioning engine for ARS workflows
# ----------------------------------------------------------------------------
# One script provisions/disables users in N SCIM 2.0 targets. Attribute
# mappings live in the separate UNITE-SCIMMappings ScriptModule - do not
# edit this file to add a new app.
#
# Wire each per-app function (Provision-<App> / Disable-<App>) to a
# PowerShellActivity in the UNITE Provisioning Hub workflow. Reference both
# THIS module and UNITE-SCIMMappings from each activity.
#
# Brought to you by iC Consult • Identity & Access Management specialists
# We build IAM solutions that don't break.  sales@ic-consult.com
# ============================================================================

# Self-signed TLS bypass for the lab. COMMENT OUT IN PRODUCTION.
try {
    Add-Type -Language CSharp -ErrorAction SilentlyContinue @"
        using System.Net;
        using System.Net.Security;
        using System.Security.Cryptography.X509Certificates;
        public class TrustAllCertsPolicy : ICertificatePolicy {
            public bool CheckValidationResult(ServicePoint sp, X509Certificate cert,
                WebRequest req, int problem) { return true; }
        }
"@
    [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]'Tls12,Tls13'
} catch { }


# ============================================================================
# PUBLIC ENTRY POINTS - wire these into the workflow IfElse branches
# ============================================================================

function Provision-HRConnect        { _Do-Provision -AppKey "HRConnect"        -Request $Request }
function Disable-HRConnect          { _Do-Disable   -AppKey "HRConnect"        -Request $Request }

function Provision-ITHelpdeskPortal { _Do-Provision -AppKey "ITHelpdeskPortal" -Request $Request }
function Disable-ITHelpdeskPortal   { _Do-Disable   -AppKey "ITHelpdeskPortal" -Request $Request }

function Provision-FinanceSuite     { _Do-Provision -AppKey "FinanceSuite"     -Request $Request }
function Disable-FinanceSuite       { _Do-Disable   -AppKey "FinanceSuite"     -Request $Request }

# Pattern for new apps:
# function Provision-JiraCloud      { _Do-Provision -AppKey "JiraCloud"        -Request $Request }
# function Disable-JiraCloud        { _Do-Disable   -AppKey "JiraCloud"        -Request $Request }


# ============================================================================
# CORE DISPATCH
# ============================================================================

function _Do-Provision {
    param([string]$AppKey, $Request)
    try {
        $ctx  = _Get-SCIMContext -AppKey $AppKey
        $user = _Get-UserAttributes -Request $Request
        if (-not $user.sAMAccountName) {
            _ReportError -AppKey $AppKey -Action "Provision" -Message "Could not read sAMAccountName from $($Request.DN)"
            return
        }

        $existing = _Find-SCIMUser -Ctx $ctx -UserName $user.sAMAccountName
        if ($existing) {
            $body = _Build-PatchActive -Active $true
            $resp = _Invoke-SCIM -Method "PATCH" -Url ("{0}/{1}" -f $ctx.Uri, $existing.id) -Token $ctx.Token -Body $body
            _Report -AppKey $AppKey -Action "Provision" -Outcome "Re-enabled existing SCIM user" -ScimId $existing.id
        } else {
            $body = _Build-CreateUser -AppKey $AppKey -User $user
            $resp = _Invoke-SCIM -Method "POST" -Url $ctx.Uri -Token $ctx.Token -Body $body
            _Report -AppKey $AppKey -Action "Provision" -Outcome "Created SCIM user" -ScimId $resp.id
        }
    } catch {
        _ReportError -AppKey $AppKey -Action "Provision" -Message (_Classify-NoResponse $_)
    }
}

function _Do-Disable {
    param([string]$AppKey, $Request)
    try {
        $ctx  = _Get-SCIMContext -AppKey $AppKey
        $user = _Get-UserAttributes -Request $Request
        if (-not $user.sAMAccountName) {
            _ReportError -AppKey $AppKey -Action "Disable" -Message "Could not read sAMAccountName from $($Request.DN)"
            return
        }

        $existing = _Find-SCIMUser -Ctx $ctx -UserName $user.sAMAccountName
        if (-not $existing) {
            _Report -AppKey $AppKey -Action "Disable" -Outcome "No-op: user not present in target"
            return
        }

        $body = _Build-PatchActive -Active $false
        $resp = _Invoke-SCIM -Method "PATCH" -Url ("{0}/{1}" -f $ctx.Uri, $existing.id) -Token $ctx.Token -Body $body
        _Report -AppKey $AppKey -Action "Disable" -Outcome "Disabled SCIM user" -ScimId $existing.id
    } catch {
        _ReportError -AppKey $AppKey -Action "Disable" -Message (_Classify-NoResponse $_)
    }
}


# ============================================================================
# MAPPING ENGINE - reads $script:SCIMMappings from UNITE-SCIMMappings
# ============================================================================

function _Build-CreateUser {
    param([string]$AppKey, $User)

    $mapping = Get-SCIMMapping -AppKey $AppKey
    $payload = [ordered]@{
        schemas = @("urn:ietf:params:scim:schemas:core:2.0:User")
    }
    $hasEnterprise = $false

    foreach ($scimPath in $mapping.Keys) {
        $source = $mapping[$scimPath]
        $value  = _Resolve-MappingValue -Source $source -User $User
        if ($null -eq $value -or $value -eq "") { continue }

        # Normalize booleans coming through as strings (e.g. "=true")
        if ($value -is [string] -and ($value -eq "true" -or $value -eq "false")) {
            $value = [bool]::Parse($value)
        }

        if ($scimPath -like "enterprise.*") { $hasEnterprise = $true }
        _Set-SCIMPath -Payload $payload -Path $scimPath -Value $value
    }

    if ($hasEnterprise) {
        $payload.schemas += "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"
    }
    return $payload
}

function _Resolve-MappingValue {
    param($Source, $User)

    if ($Source -is [scriptblock]) {
        try { return (& $Source $User) } catch { return $null }
    }
    if ($Source -is [string]) {
        if ($Source.StartsWith("=")) { return $Source.Substring(1) }
        # AD attribute lookup. Hashtables and PSObjects both indexable as $User.<name>.
        return $User.$Source
    }
    return $Source
}

# Walks a path like "name.givenName", "emails[0].value", or "enterprise.department"
# and writes the value into the payload hashtable, creating intermediate
# nodes as needed.
function _Set-SCIMPath {
    param($Payload, [string]$Path, $Value)

    $enterpriseUrn = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"

    # Re-key "enterprise.*" to the urn-prefixed extension key.
    if ($Path -like "enterprise.*") {
        $rest = $Path.Substring("enterprise.".Length)
        if (-not $Payload.Contains($enterpriseUrn)) {
            $Payload[$enterpriseUrn] = [ordered]@{}
        }
        _Set-NestedPath -Node $Payload[$enterpriseUrn] -Segments (_Tokenize-Path $rest) -Value $Value
        return
    }

    _Set-NestedPath -Node $Payload -Segments (_Tokenize-Path $Path) -Value $Value
}

# Tokenize "emails[0].value" -> @("emails", "[0]", "value")
function _Tokenize-Path {
    param([string]$Path)
    $tokens = @()
    foreach ($part in $Path.Split('.')) {
        $m = [regex]::Match($part, '^([^\[]+)(\[(\d+)\])?$')
        if ($m.Success) {
            $tokens += $m.Groups[1].Value
            if ($m.Groups[2].Success) { $tokens += "[$($m.Groups[3].Value)]" }
        } else {
            $tokens += $part
        }
    }
    return $tokens
}

function _Set-NestedPath {
    param($Node, [object[]]$Segments, $Value)

    for ($i = 0; $i -lt $Segments.Count; $i++) {
        $seg     = $Segments[$i]
        $isLast  = ($i -eq $Segments.Count - 1)
        $isIndex = $seg -match '^\[(\d+)\]$'

        if ($isIndex) {
            $idx = [int]$Matches[1]
            # $Node is expected to be a System.Collections.ArrayList from previous step
            while ($Node.Count -le $idx) { [void]$Node.Add([ordered]@{}) }
            if ($isLast) { $Node[$idx] = $Value } else { $Node = $Node[$idx] }
            continue
        }

        # Look ahead: is the next segment an array index?
        $nextIsIndex = (-not $isLast) -and ($Segments[$i+1] -match '^\[\d+\]$')

        if ($isLast) {
            $Node[$seg] = $Value
        } else {
            if (-not $Node.Contains($seg)) {
                if ($nextIsIndex) {
                    $Node[$seg] = New-Object System.Collections.ArrayList
                } else {
                    $Node[$seg] = [ordered]@{}
                }
            }
            $Node = $Node[$seg]
        }
    }
}

function _Build-PatchActive {
    param([bool]$Active)
    return [ordered]@{
        schemas    = @("urn:ietf:params:scim:api:messages:2.0:PatchOp")
        Operations = @(
            [ordered]@{ op = "replace"; path = "active"; value = $Active }
        )
    }
}


# ============================================================================
# AD READ + WORKFLOW CONFIG
# ============================================================================

function _Get-UserAttributes {
    param($Request)
    $u = Get-QADUser $Request.DN -DontUseDefaultIncludedProperties `
        -IncludedProperties sAMAccountName,givenName,sn,mail,displayName,department,title,employeeID,
                             telephoneNumber,mobile,company,manager,extensionAttribute1,extensionAttribute2,
                             extensionAttribute3,extensionAttribute4,extensionAttribute5
    if (-not $u) { throw "Get-QADUser returned nothing for $($Request.DN)" }
    return $u
}

function _Get-SCIMContext {
    param([string]$AppKey)

    $uriParam   = "SCIM-$AppKey-URI"
    $tokenParam = "SCIM-$AppKey-Token"

    $uri   = $Workflow.Parameter($uriParam)
    $token = $Workflow.Parameter($tokenParam)

    # Trim BOTH ends - MMC sometimes saves values with a leading space.
    if ($uri)   { $uri   = $uri.Trim() }
    if ($token) { $token = $token.Trim() }

    if (-not $uri)   { throw "Workflow parameter '$uriParam' is empty. Open the workflow's Parameters dialog in MMC and set it." }
    if (-not $token) { throw "Workflow parameter '$tokenParam' is empty. Open the workflow's Parameters dialog in MMC, click in the value field, paste the raw token (without scim_ prefix), and OK. If you see ******** but get this error, the previous save did not persist - re-enter it." }

    # SCIMServer mints tokens with a 'scim_' prefix and that prefix is part of
    # the bearer value. Admins pasting via MMC usually paste the raw mint value
    # WITHOUT the prefix. Add it defensively if missing.
    if (-not $token.StartsWith("scim_")) { $token = "scim_$token" }

    # Tolerate trailing /Users - script appends it itself.
    $uri = $uri.TrimEnd('/')
    if ($uri.EndsWith("/Users")) { $uri = $uri.Substring(0, $uri.Length - "/Users".Length) }

    return @{
        AppKey = $AppKey
        Uri    = "$uri/Users"
        Token  = $token
        DryRun = ($Workflow.Parameter("SCIM-DryRun") -eq "true")
    }
}


# ============================================================================
# SCIM HTTP
# ============================================================================

function _Find-SCIMUser {
    param($Ctx, [string]$UserName)
    if ($Ctx.DryRun) { return $null }

    $filter = [System.Web.HttpUtility]::UrlEncode(("userName eq `"{0}`"" -f $UserName))
    $url    = "$($Ctx.Uri)?filter=$filter"
    $resp   = _Invoke-SCIM -Method "GET" -Url $url -Token $Ctx.Token -Body $null
    if ($resp.Resources -and $resp.Resources.Count -gt 0) { return $resp.Resources[0] }
    return $null
}

function _Invoke-SCIM {
    param([string]$Method, [string]$Url, [string]$Token, $Body)

    $headers = @{
        "Authorization" = "Bearer $Token"
        "Accept"        = "application/scim+json"
    }
    $params = @{
        Method  = $Method
        Uri     = $Url
        Headers = $headers
        UseBasicParsing = $true
        ErrorAction = "Stop"
    }
    if ($Body) {
        $params["ContentType"] = "application/scim+json"
        $params["Body"]        = ($Body | ConvertTo-Json -Depth 10 -Compress)
    }

    $raw = Invoke-WebRequest @params
    if ($raw.Content) { return ($raw.Content | ConvertFrom-Json) }
    return $null
}


# ============================================================================
# REPORTING / ERROR CLASSIFICATION
# ============================================================================

function _Report {
    param([string]$AppKey, [string]$Action, [string]$Outcome, [string]$ScimId = $null)
    $line = "[$AppKey/$Action] $Outcome"
    if ($ScimId) { $line += " (scimId=$ScimId)" }
    # ARS captures Write-Host output into the workflow Change History entry.
    # PowerShellRequest does not expose a SetWorkflowLog method in 8.x.
    Write-Host $line
}

function _ReportError {
    param([string]$AppKey, [string]$Action, [string]$Message)
    $line = "[$AppKey/$Action] ERROR: $Message"
    Write-Host $line
    # throw surfaces the message into the workflow Change History as an error.
    throw $line
}

function _Classify-NoResponse {
    param($Err)
    $msg = if ($Err.Exception.Message) { $Err.Exception.Message } else { "$Err" }
    $resp = $Err.Exception.Response
    if ($resp) {
        $code = [int]$resp.StatusCode
        switch ($code) {
            401 { return "401 Unauthorized - token is wrong, missing 'Bearer ' prefix, or expired" }
            403 { return "403 Forbidden - token lacks permission on this Connected System" }
            404 { return "404 Not Found - check the URI; the slug may not match a Connected System" }
            409 { return "409 Conflict - user already exists; race condition between Find and Create" }
            429 { return "429 Rate limited - back off and retry" }
            default {
                if ($code -ge 500) { return "$code Server error - check SCIMServer logs" }
                return "$code $msg"
            }
        }
    }
    if ($msg -match "actively refused|No connection could be made") { return "Connection refused - is SCIMServer running on the configured host:port?" }
    if ($msg -match "remote name could not be resolved")             { return "DNS failure - check the host in the URI" }
    if ($msg -match "timed out")                                     { return "Timeout - SCIMServer did not respond" }
    if ($msg -match "SSL|TLS|certificate")                           { return "TLS error - self-signed cert; enable the TrustAllCertsPolicy block at top of script for lab use" }
    return $msg
}
