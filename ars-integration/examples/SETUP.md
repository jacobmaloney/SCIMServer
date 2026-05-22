# UNITE Examples — Bonus Day Off (setup)

Single workflow that demonstrates eight common ARS patterns. Audience sees
each pattern as a distinctly named activity in Change History.

| # | Example | Where it lives |
|---|---|---|
| 1 | Virtual attribute | MMC config — see below |
| 2 | Pass parameter to script | Workflow Parameter `BonusDayCap`, read by `Ex2-ReadContext` |
| 3 | Return value from script | `Ex3-PublishResult` — Write-Output + throw |
| 4 | Web Interface button | Web Interface customization — see below |
| 5 | AddRecordToReport | Three activities in the workflow XAML |
| 6 | IfElse branching | "Ex 6: IfElse - under cap?" |
| 7 | Set-QADObject write-back | `Ex7-GrantBonusDay` |
| 8 | Approval | "Ex 8: Approval - manager approves" |

---

## Prerequisites

Both virtual attributes must exist on the user class **before** running the
deploy script, or workflow validation will fail.

### Ex 1 — Create the virtual attributes (MMC)

1. Open **Active Roles Console** as an admin
2. Navigate to **Configuration → Server Configuration → Schema → Active Directory → user**
3. Right-click `user` → **New → Active Roles Virtual Attribute**
4. Create the first attribute:
   - Name: `edsva-BonusDayRequest`
   - Display name: `Bonus Day Request (reason)`
   - Syntax: **Unicode String**
   - Multi-valued: **No**
5. Create the second attribute:
   - Name: `edsva-BonusDaysGranted`
   - Display name: `Bonus Days Granted`
   - Syntax: **Integer**
   - Multi-valued: **No**
6. Restart the **Active Roles Administration Service** (so the new attributes
   become available to workflows).

### Ex 4 — Create the Web Interface button

1. Open **Active Roles Web Interface Sites Configuration**
2. Pick the site (usually `ARWebAdmin`) → **Customize**
3. In the customization tool, **Directory Management → Users → Command**
4. Add a new command:
   - Title: **Grant Bonus Day**
   - Description: `Request a bonus day off for this user (needs manager approval)`
   - Action: **Run a script** → New script:
     ```powershell
     param($Request)
     # Pop up a reason prompt and write it to the virtual attribute.
     # The "UNITE Examples - Bonus Day Off" workflow fires on this modify.
     $reason = Read-Host "Reason for bonus day"
     if ([string]::IsNullOrWhiteSpace($reason)) {
         throw "Reason is required."
     }
     Set-QADObject $Request.DN -ObjectAttributes @{ 'edsva-BonusDayRequest' = $reason }
     ```
5. **Save** and **reload** the Web Interface site so the command shows up on
   the user view.

> Note: the WI "Run a script" flow varies slightly between AR 8.0/8.1/8.2.
> If the new command doesn't appear, restart `World Wide Web Publishing
> Service` and clear the WI cache (`...\Web Interface\<site>\App_Data\Cache`).

---

## Deploy the script module + workflow

After the prerequisites are in place:

```powershell
& "C:\Users\jacob\source\repos\SCIMServer\ars-integration\examples\Deploy-UNITEBonusDayWorkflow.ps1"
```

The script:
- Upserts the `UNITE-BonusDay` script module from `UNITE-BonusDay.ps1`.
- Upserts the `UNITE Examples - Bonus Day Off` workflow.
- Declares one workflow parameter `BonusDayCap` (default `5`).
- Operation trigger: `ModifyObject` of `edsva-BonusDayRequest` on the
  `user` class, scoped to the **UNITE-2026** container.

Re-runnable — finds rows by name, updates them in place. Trigger 5 cache-bust
rows confirm the in-memory ARS cache picked up the changes.

---

## Demo flow at the talk

1. Show the user page in Web Interface. Point out the new **Grant Bonus
   Day** command.
2. Click it. Type a reason ("Birthday").
3. Switch to the Active Roles Console **Reports / Change History** view.
4. Walk the audience down the activity list:

   | Activity name | Concept being demoed |
   |---|---|
   | `Ex 5a: AddRecordToReport` | Example 5: static text in Change History |
   | `Ex 2: PS read workflow parameter + AD attributes` | Example 2 + bonus AD-read |
   | `Ex 8: Approval - manager approves` | Example 8: out-of-band approval |
   | `Ex 6: IfElse - under cap?` | Example 6: token-condition branching |
   | `Ex 7: PS Set-QADObject grant` | Example 7: write-back to AD |
   | `Ex 3: PS return new value to next step` | Example 3: return-value channels |
   | `Ex 5b: AddRecordToReport (granted)` OR `Ex 5c: AddRecordToReport (denied)` | Example 5 + Example 6 outcome |

5. Reset for the next demo: in MMC, edit the target user → set
   `edsva-BonusDaysGranted` back to `0`. The "Grant Bonus Day" button works
   again.

---

## Troubleshooting

- **"The workflow failed validation"** on `Set-QADObject` — almost always
  caused by referencing a virtual attribute that doesn't exist yet. Run Ex 1
  manually first, restart the AR Admin Service, then re-deploy.
- **Approval activity not firing** — check that the demo user has a
  populated `manager` attribute pointing to another user with rights to
  approve in your AR Configuration.
- **Workflow row updates but behavior doesn't change** — ARS caches workflow
  definitions in-memory. The deploy script triggers a cache-bust via the
  trigger-row count (`Rows: 5` in the deploy output). If you only see
  `Rows: 1`, the cache may be stale — disable + re-enable the workflow in
  MMC, or restart the AR Admin Service.
