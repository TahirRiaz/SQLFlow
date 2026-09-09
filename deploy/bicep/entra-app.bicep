// Provisions the Microsoft Entra app users sign in with, so the estate's single sign-on is managed as code
// instead of by hand. It creates three directory objects through the Microsoft Graph Bicep extension:
//
//   application       an SPA registration with the GUI origin as its redirect URI, carrying one app role,
//                     SqlFlow.User, that gates who may sign in. Single-tenant by default; multi-tenant when a
//                     second organization also needs to sign in (see signInAudience below).
//   servicePrincipal  the enterprise application, with appRoleAssignmentRequired set: Entra then refuses to
//                     issue a token to anyone who is not assigned the app role, so "any tenant user" becomes
//                     "only assigned members".
//   appRoleAssignedTo an assignment of the SqlFlow.User role to one security group in THIS app's home tenant,
//                     when its object id is given. Every member of that group (and only them) can sign in.
//
// The deploying principal needs directory write access (Application Administrator or the
// Application.ReadWrite.All app permission) for the Graph resources, and assigning a GROUP to the role needs
// Microsoft Entra ID P1. The extension is enabled in bicepconfig.json in this directory.
//
// With assignment required and no group id, NO ONE can sign in until members are assigned (here on the next
// deploy, or by hand in the enterprise application). That is the secure-by-default posture: access is denied
// until a group is named, never open to the whole tenant.
//
// When signInAudience is multi-tenant, appRoleAssignmentRequired still applies, but this template cannot grant
// role assignments in a FOREIGN tenant (the deploying principal has no directory write there). Each additional
// tenant's own admin must: (1) consent to the app once (creates their own local enterprise application object),
// then (2) assign the SqlFlow.User role to their own users/groups on that local object, entirely within their
// own tenant. See main.bicep's entraForeignTenantReminder output for the exact next step.

extension microsoftGraphV1

@description('Display name of the app registration as it appears in Entra and on the consent prompt.')
param displayName string = 'SQLFlow'

@description('Stable unique name for the registration (the Graph identity key used to find and update it across deploys). Lowercase, no spaces.')
param uniqueName string = 'sqlflow'

@description('The exact GUI origin to register as the SPA redirect URI, e.g. https://sqlflow-gui.<env>.azurecontainerapps.io. MSAL signs in against window.location.origin, so this is the origin with no trailing path.')
param redirectUri string

@description('Object id of the Entra security group whose members may sign in. Its members are assigned the SqlFlow.User role. Empty leaves the app with assignment required but no members, so assign a group or users in the enterprise application before anyone can sign in. Only reaches users in THIS app\'s home tenant; a foreign tenant assigns its own users itself once it has consented (see redeploy notes above).')
param allowedGroupObjectId string = ''

@description('AzureADMyOrg (default) restricts sign-in to this app\'s home tenant. AzureADMultipleOrgs additionally allows any tenant that has consented to the app; the control plane\'s own AllowedTenantIds is the actual gate on which of those tenants are trusted.')
@allowed(['AzureADMyOrg', 'AzureADMultipleOrgs'])
param signInAudience string = 'AzureADMyOrg'

// A deterministic id for the app role, stable across deploys so re-running the template updates the same role
// rather than churning a new one. Roles are keyed by id inside the application, so this must not change once
// members are assigned against it.
var appRoleId = guid(uniqueName, 'SqlFlow.User')

// Microsoft Graph's well-known app id, and the id of its User.Read delegated permission: the baseline consent
// a sign-in SPA needs so the account can read its own profile.
var microsoftGraphAppId = '00000003-0000-0000-c000-000000000000'
var userReadScopeId = 'e1fe6dd8-ba31-4d61-89e7-88639da4683d'

resource application 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: uniqueName
  displayName: displayName
  signInAudience: signInAudience
  spa: {
    redirectUris: [
      redirectUri
    ]
  }
  appRoles: [
    {
      id: appRoleId
      allowedMemberTypes: [
        'User'
      ]
      displayName: 'SQLFlow User'
      description: 'May sign in to SQLFlow. Assignment is required, so only members of this role reach the app.'
      value: 'SqlFlow.User'
      isEnabled: true
    }
  ]
  requiredResourceAccess: [
    {
      resourceAppId: microsoftGraphAppId
      resourceAccess: [
        {
          id: userReadScopeId
          type: 'Scope'
        }
      ]
    }
  ]
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: application.appId
  // The gate: Entra issues a token only to users assigned an app role on this enterprise application.
  appRoleAssignmentRequired: true
}

// Assign the whole group to the SqlFlow.User role in one grant. Entra expands group membership at sign-in, so
// adding or removing a person from the group changes their access with no redeploy.
resource groupAssignment 'Microsoft.Graph/appRoleAssignedTo@v1.0' = if (!empty(allowedGroupObjectId)) {
  appRoleId: appRoleId
  principalId: allowedGroupObjectId
  resourceId: servicePrincipal.id
}

@description('Client (application) id of the registration: pass to the control plane as its AzureAd ClientId.')
output clientId string = application.appId

@description('Object id of the enterprise application (service principal).')
output servicePrincipalObjectId string = servicePrincipal.id

@description('Value of the app role that gates sign-in; appears in the token roles claim for assigned users.')
output appRoleValue string = 'SqlFlow.User'
