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
# PUBLIC ENTRY POINTS
# ----------------------------------------------------------------------------
# Workflow XAML should call Dispatch-AllSCIM from a SINGLE PowerShellActivity.
# This function loops over every app in the mapping table, checks whether the
# matching SCIM-<App> virtual attribute changed in THIS submit, and routes
# the change to Provision or Remove. Multiple checkboxes toggled in one
# submit all get handled in one workflow run.
#
# Removal policy (Disable vs Delete) is per-app, read from $script:SCIMAppConfig
# in UNITE-SCIMMappings. Defaults to Disable.
# ============================================================================

function Dispatch-AllSCIM {
    if (-not $script:SCIMMappings) {
        throw "SCIMMappings not loaded - the mapping table from UNITE-SCIMMappings must be concatenated into this ScriptModule."
    }
    foreach ($app in $script:SCIMMappings.Keys) {
        # $Request.Get returns the new value only when the attribute was modified
        # in this submit. Empty/null otherwise - so untouched apps are skipped.
        $newValue = "$($Request.Get("SCIM-$app"))"
        if ([string]::IsNullOrEmpty($newValue)) { continue }

        if ($newValue -ieq "true")  { _Do-Provision -AppKey $app -Request $Request }
        elseif ($newValue -ieq "false") { _Do-Remove -AppKey $app -Request $Request }
        # any other value (shouldn't happen for a Boolean virtual attr) - log and skip
        else {
            Write-Output "[$app] Unexpected SCIM-$app value '$newValue' - skipping (expected 'true' or 'false')"
        }
    }
}

# Legacy per-app entry points kept for back-compat with workflow XAML that
# still wires individual Provision-/Disable- branches. New deployments should
# use the single Dispatch-AllSCIM activity above.
function Provision-HRConnect        { _Do-Provision -AppKey "HRConnect"        -Request $Request }
function Disable-HRConnect          { _Do-Remove    -AppKey "HRConnect"        -Request $Request }
function Provision-ITHelpdeskPortal { _Do-Provision -AppKey "ITHelpdeskPortal" -Request $Request }
function Disable-ITHelpdeskPortal   { _Do-Remove    -AppKey "ITHelpdeskPortal" -Request $Request }
function Provision-FinanceSuite     { _Do-Provision -AppKey "FinanceSuite"     -Request $Request }
function Disable-FinanceSuite       { _Do-Remove    -AppKey "FinanceSuite"     -Request $Request }


# ============================================================================
# CORE DISPATCH
# ============================================================================

function _Do-Provision {
    param([string]$AppKey, $Request)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $ctx  = _Get-SCIMContext -AppKey $AppKey
        $user = _Get-UserAttributes -Request $Request
        if (-not $user.sAMAccountName) {
            _ReportError -AppKey $AppKey -Action "Provision" -Message "Could not read sAMAccountName from $($Request.DN)"
            return
        }

        $sam = "$($user.sAMAccountName)"
        $existing = _Find-SCIMUser -Ctx $ctx -UserName $sam
        if ($existing) {
            if ($existing.active -eq $true) {
                $sw.Stop()
                _Report -AppKey $AppKey -Action "Provision" -Verb "Already active" -UserName $sam -ScimId $existing.id -Note "no-op" -ElapsedMs $sw.ElapsedMilliseconds
                return
            }
            $body = _Build-PatchActive -Active $true
            $resp = _Invoke-SCIM -Method "PATCH" -Url ("{0}/{1}" -f $ctx.Uri, $existing.id) -Token $ctx.Token -Body $body
            $sw.Stop()
            _Report -AppKey $AppKey -Action "Provision" -Verb "Re-enabled" -UserName $sam -ScimId $existing.id -ElapsedMs $sw.ElapsedMilliseconds
        } else {
            $body = _Build-CreateUser -AppKey $AppKey -User $user
            $resp = _Invoke-SCIM -Method "POST" -Url $ctx.Uri -Token $ctx.Token -Body $body
            $sw.Stop()
            _Report -AppKey $AppKey -Action "Provision" -Verb "Created" -UserName $sam -ScimId $resp.id -ElapsedMs $sw.ElapsedMilliseconds
        }
    } catch {
        $sw.Stop()
        _ReportError -AppKey $AppKey -Action "Provision" -Message (_Classify-NoResponse $_)
    }
}

# Dispatch helper: read the per-app OnRemove policy and route to Disable or Delete.
function _Do-Remove {
    param([string]$AppKey, $Request)
    $cfg = Get-SCIMAppConfig -AppKey $AppKey
    $policy = if ($cfg -and $cfg.OnRemove) { $cfg.OnRemove } else { "Disable" }
    if ($policy -ieq "Delete") {
        _Do-Delete  -AppKey $AppKey -Request $Request
    } else {
        _Do-Disable -AppKey $AppKey -Request $Request
    }
}

function _Do-Disable {
    param([string]$AppKey, $Request)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $ctx  = _Get-SCIMContext -AppKey $AppKey
        $user = _Get-UserAttributes -Request $Request
        if (-not $user.sAMAccountName) {
            _ReportError -AppKey $AppKey -Action "Disable" -Message "Could not read sAMAccountName from $($Request.DN)"
            return
        }

        $sam = "$($user.sAMAccountName)"
        $existing = _Find-SCIMUser -Ctx $ctx -UserName $sam
        if (-not $existing) {
            $sw.Stop()
            _Report -AppKey $AppKey -Action "Disable" -Verb "Skipped" -UserName $sam -Note "user not present in target - no-op" -ElapsedMs $sw.ElapsedMilliseconds
            return
        }
        if ($existing.active -eq $false) {
            $sw.Stop()
            _Report -AppKey $AppKey -Action "Disable" -Verb "Already inactive" -UserName $sam -ScimId $existing.id -Note "no-op" -ElapsedMs $sw.ElapsedMilliseconds
            return
        }

        $body = _Build-PatchActive -Active $false
        $resp = _Invoke-SCIM -Method "PATCH" -Url ("{0}/{1}" -f $ctx.Uri, $existing.id) -Token $ctx.Token -Body $body
        $sw.Stop()
        _Report -AppKey $AppKey -Action "Disable" -Verb "Disabled" -UserName $sam -ScimId $existing.id -ElapsedMs $sw.ElapsedMilliseconds
    } catch {
        $sw.Stop()
        _ReportError -AppKey $AppKey -Action "Disable" -Message (_Classify-NoResponse $_)
    }
}

function _Do-Delete {
    param([string]$AppKey, $Request)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $ctx  = _Get-SCIMContext -AppKey $AppKey
        $user = _Get-UserAttributes -Request $Request
        if (-not $user.sAMAccountName) {
            _ReportError -AppKey $AppKey -Action "Delete" -Message "Could not read sAMAccountName from $($Request.DN)"
            return
        }

        $sam = "$($user.sAMAccountName)"
        $existing = _Find-SCIMUser -Ctx $ctx -UserName $sam
        if (-not $existing) {
            $sw.Stop()
            _Report -AppKey $AppKey -Action "Delete" -Verb "Skipped" -UserName $sam -Note "user not present in target - no-op" -ElapsedMs $sw.ElapsedMilliseconds
            return
        }

        $resp = _Invoke-SCIM -Method "DELETE" -Url ("{0}/{1}" -f $ctx.Uri, $existing.id) -Token $ctx.Token -Body $null
        $sw.Stop()
        _Report -AppKey $AppKey -Action "Delete" -Verb "Deleted" -UserName $sam -ScimId $existing.id -ElapsedMs $sw.ElapsedMilliseconds
    } catch {
        $sw.Stop()
        _ReportError -AppKey $AppKey -Action "Delete" -Message (_Classify-NoResponse $_)
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
    if (-not $token) { throw "Workflow parameter '$tokenParam' is empty. Open the workflow's Parameters dialog in MMC, click in the value field, paste the FULL bearer token (exactly as your SCIM provider gave you - SCIMServer mints with a 'scim_' prefix, Okta/Auth0/etc. don't), and OK. If you see ******** but get this error, the previous save did not persist - re-enter it." }

    # NOTE: the engine deliberately does NOT munge the token. Whatever the
    # admin pasted is what gets sent verbatim after 'Bearer '. SCIMServer
    # happens to mint tokens with a 'scim_' prefix; other SCIM providers
    # don't. The token format is the SCIM provider's contract, not ours.

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
    param(
        [string]$AppKey,
        [string]$Action,
        [string]$Verb,           # "Created", "Re-enabled", "Disabled", "Skipped"
        [string]$UserName,
        [string]$ScimId = $null,
        [string]$Note   = $null,
        [long]  $ElapsedMs = 0
    )
    # Human-readable label per AppKey (camelCase keys -> spaced labels).
    $appLabel = switch ($AppKey) {
        "HRConnect"         { "HR Connect" }
        "ITHelpdeskPortal"  { "IT Helpdesk Portal" }
        "FinanceSuite"      { "Finance Suite" }
        default             { $AppKey }
    }

    # Build a one-line headline that ARS Change History will display in full.
    $headline = "$Verb '$UserName' in $appLabel"
    if ($ScimId) { $headline += " - SCIM id $ScimId" }
    if ($Note)   { $headline += " - $Note" }
    if ($ElapsedMs -gt 0) { $headline += " - ${ElapsedMs}ms" }

    # Write-Output goes to the workflow pipeline and ARS captures it as the
    # activity's return value, which renders in Change History below the
    # "Activity successfully performed the script" line.
    Write-Output $headline
}

function _ReportError {
    param([string]$AppKey, [string]$Action, [string]$Message)
    $appLabel = switch ($AppKey) {
        "HRConnect"         { "HR Connect" }
        "ITHelpdeskPortal"  { "IT Helpdesk Portal" }
        "FinanceSuite"      { "Finance Suite" }
        default             { $AppKey }
    }
    # throw is the ONLY thing ARS surfaces as an error in Change History.
    throw "[$appLabel/$Action] $Message"
}

function _Classify-NoResponse {
    param($Err)
    $msg = if ($Err.Exception.Message) { $Err.Exception.Message } else { "$Err" }
    $resp = $Err.Exception.Response

    # Capture the SCIM error response body when there is one - its 'detail'
    # field names the real failure (e.g. "NullReferenceException: ...").
    $body = ""
    if ($resp) {
        try {
            $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $body = $sr.ReadToEnd(); $sr.Close()
            if ($body.Length -gt 400) { $body = $body.Substring(0, 400) + "..." }
        } catch { }
    }
    $bodyTail = if ($body) { " - server said: $body" } else { "" }

    if ($resp) {
        switch ([int]$resp.StatusCode) {
            401 { return "401 Unauthorized - token rejected. Paste the FULL 'scim_<value>' mint string into the workflow token parameter.$bodyTail" }
            403 { return "403 Forbidden - token is valid but scoped to a different Connected System.$bodyTail" }
            404 { return "404 Not Found - the URI's tenant slug does not match any Connected System.$bodyTail" }
            409 { return "409 Conflict - user already exists; benign race between Find and Create.$bodyTail" }
            429 { return "429 Rate limited - back off and retry.$bodyTail" }
            default {
                $code = [int]$resp.StatusCode
                if ($code -ge 500) { return "$code Server error.$bodyTail" }
                return "$code $msg.$bodyTail"
            }
        }
    }
    if ($msg -match "actively refused|No connection could be made") { return "Connection refused - is SCIMServer running on the configured host:port?" }
    if ($msg -match "remote name could not be resolved")             { return "DNS failure - check the host in the URI" }
    if ($msg -match "timed out")                                     { return "Timeout - SCIMServer did not respond" }
    if ($msg -match "SSL|TLS|certificate")                           { return "TLS error - self-signed cert; enable the TrustAllCertsPolicy block at top of script for lab use" }
    return $msg
}
