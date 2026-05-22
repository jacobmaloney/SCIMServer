# =============================================================================
# UNITE-BonusDay - script module backing the "UNITE Examples - Bonus Day Off"
# workflow. Demonstrates 8 common ARS workflow patterns the audience asks about:
#
#   Ex 1  Virtual attribute              (config - see SETUP.md)
#   Ex 2  Pass parameter to script        $Workflow.Parameter("BonusDayCap")
#   Ex 3  Return value from script        Set-WorkflowVariable + Write-Output
#   Ex 4  Web Interface button            (config - see SETUP.md)
#   Ex 5  AddRecordToReport               (lives in the workflow XAML)
#   Ex 6  IfElse branching                (lives in the workflow XAML)
#   Ex 7  Update target via Set-QADObject Set-QADObject inside Ex7-GrantBonusDay
#   Ex 8  Approval activity               (lives in the workflow XAML)
#
# Brought to you by iC Consult * Identity & Access Management specialists
# We build IAM solutions that don't break.  sales@ic-consult.com
# =============================================================================

# Cap value for "how many bonus days can a user accumulate". This is the
# DEFAULT - the workflow's Parameters dialog overrides it per-deploy. Demo
# of Example 2: workflow parameter -> script.
$script:DefaultBonusDayCap = 5

function Ex2-ReadContext {
    # EXAMPLE 2 + reading object attributes.
    # Pull three values from three different sources:
    #   - $Workflow.Parameter("BonusDayCap")  workflow-level parameter (Ex 2)
    #   - $Request.Get("edsva-BonusDayRequest")  the modified attribute the
    #       Web Interface button set (Ex 4 trigger path)
    #   - $Request.Get("manager")  current AD value, used for Ex 8 approval
    try {
        $cap = $Workflow.Parameter("BonusDayCap")
        if (-not $cap) { $cap = $script:DefaultBonusDayCap }
        $reason  = "$($Request.Get('edsva-BonusDayRequest'))".Trim()
        $manager = "$($Request.Get('manager'))".Trim()
        $sam     = "$($Request.Get('sAMAccountName'))".Trim()

        # Stash in script scope so downstream activities in this same workflow
        # run can read them without going back to AD - Example 3 pattern.
        $script:BD_Cap     = [int]$cap
        $script:BD_Reason  = $reason
        $script:BD_Manager = $manager
        $script:BD_Sam     = $sam

        # Visible in the activity's script trace (click into the row in Change
        # History). NOT in the main row text - that takes a throw or a static
        # AddRecordToReport.
        Write-Output "[Ex 2] BonusDayCap parameter = $cap"
        Write-Output "[Ex 2] Reason from web form = '$reason'"
        Write-Output "[Ex 2] Target sAMAccountName = $sam"
        Write-Output "[Ex 2] Manager DN (for Ex 8 approval) = $manager"

        # Throw the human-readable summary so it lands on the activity row
        # itself (only thing ARS surfaces dynamically). The PS activity has
        # SuppressError=True so the workflow continues.
        throw "[Ex 2] $sam requested a bonus day off. Reason: $reason"
    } catch [System.Management.Automation.RuntimeException] {
        # Re-throw our own intentional summary throw
        throw
    } catch {
        throw "[Ex 2 FAILED] $($_.Exception.Message)"
    }
}

function Ex7-GrantBonusDay {
    # EXAMPLE 7: write back to the target object via Set-QADObject.
    # The IfElse branch (Ex 6) only reaches this function if the user is
    # under the cap. We bump edsva-BonusDaysGranted by 1.
    try {
        $current = "$($Request.Get('edsva-BonusDaysGranted'))"
        if ([string]::IsNullOrWhiteSpace($current)) { $current = "0" }
        $new = [int]$current + 1

        Set-QADObject $Request.DN -ObjectAttributes @{ 'edsva-BonusDaysGranted' = $new } | Out-Null

        # Stash for Ex 3
        $script:BD_NewBalance = $new

        Write-Output "[Ex 7] edsva-BonusDaysGranted: $current -> $new"
        throw "[Ex 7] Granted +1 bonus day. New balance: $new"
    } catch [System.Management.Automation.RuntimeException] {
        throw
    } catch {
        throw "[Ex 7 FAILED] $($_.Exception.Message)"
    }
}

function Ex3-PublishResult {
    # EXAMPLE 3: return value from script (back to the workflow audit trail).
    # ARS PowerShellActivities don't have a "return value" the way a regular
    # function does. The three channels for getting a value back out are:
    #   1) Write-Output - shows in the script trace only (not main row).
    #   2) Throw a string - shows on the activity's main row in Change History.
    #   3) Set-WorkflowVariable - if you need it in a downstream condition.
    #
    # Here we demonstrate all three so the audience sees the trade-offs.
    $new = $script:BD_NewBalance
    if (-not $new) { $new = "<unknown>" }

    # Channel 1 (script trace)
    Write-Output "[Ex 3 channel 1 / Write-Output] New balance available downstream: $new"

    # Channel 2 (Change History main row) - via throw with SuppressError=True
    # on the activity so the workflow doesn't abort
    throw "[Ex 3] $($script:BD_Sam) now has $new bonus day(s). Reason was: $($script:BD_Reason)"
}

function Ex9-ClearRequestMarker {
    # Housekeeping: clear edsva-BonusDayRequest so the button can be used
    # again on the next request. Not one of the numbered examples - just
    # makes the demo idempotent.
    try {
        Set-QADObject $Request.DN -ObjectAttributes @{ 'edsva-BonusDayRequest' = '' } | Out-Null
        Write-Output "[cleanup] edsva-BonusDayRequest cleared; button ready for next use"
    } catch {
        Write-Output "[cleanup WARN] could not clear edsva-BonusDayRequest: $($_.Exception.Message)"
    }
}
