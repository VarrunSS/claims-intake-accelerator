<#
.SYNOPSIS
    Grants the Entra app roles to the managed identities (Microsoft Graph appRoleAssignedTo). NEVER RUN: there was no tenant.
.DESCRIPTION
    Needs the Azure CLI signed in as someone allowed to grant app roles (Application Administrator or Privileged Role Administrator).
    Reads the principal ids from the azd environment outputs (FUNCTION_IDENTITY_PRINCIPAL_ID, API_IDENTITY_PRINCIPAL_ID).
    Function identity: Intake.ReadHistory (review API), Claims.Read and Policies.Read (legacy). API identity: Claims.Write (legacy).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string]$FunctionPrincipalId,
    [Parameter(Mandatory)] [string]$ApiPrincipalId,
    [string]$ReviewApiAppId = 'claims-intake-review-api',
    [string]$LegacyAppId = 'claims-intake-legacy'
)
$ErrorActionPreference = 'Stop'

function Grant([string]$principalId, [string]$resourceDisplayName, [string]$role) {
    $resource = az ad sp list --display-name $resourceDisplayName --query '[0]' -o json | ConvertFrom-Json
    $roleId = ($resource.appRoles | Where-Object value -eq $role).id
    if (-not $roleId) { throw "The role $role was not found on $resourceDisplayName." }
    if ($PSCmdlet.ShouldProcess("$principalId", "grant $role on $resourceDisplayName")) {
        $body = @{ principalId = $principalId; resourceId = $resource.id; appRoleId = $roleId } | ConvertTo-Json -Compress
        az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$($resource.id)/appRoleAssignedTo" --body $body | Out-Null
    }
}

Grant $FunctionPrincipalId $ReviewApiAppId 'Intake.ReadHistory'
Grant $FunctionPrincipalId $LegacyAppId 'Claims.Read'
Grant $FunctionPrincipalId $LegacyAppId 'Policies.Read'
Grant $ApiPrincipalId $LegacyAppId 'Claims.Write'
