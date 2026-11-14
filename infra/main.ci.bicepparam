// Parameter values for the CI checks only: `az bicep build-params`, and PSRule for Azure, which needs concrete values to expand
// main.bicep. These are placeholders (the all-zero GUID is not a real app registration); nothing is deployed from this file.
using 'main.bicep'

param environmentName = 'ci'
param reviewApiClientId = '00000000-0000-0000-0000-000000000001'
param legacyClientId = '00000000-0000-0000-0000-000000000002'
// Both stages on, so the Event Grid and role modules are expanded and checked too.
param deployEventSubscription = true
param alertEmailAddress = 'claims-ops@thamesmutual.example'
