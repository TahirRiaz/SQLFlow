
# Managing Environment Variables in .NET Core Applications

This guide describes how to reference an environment variable named `SQLFlowConStr` in the `appsettings.json` file of a .NET Core Blazor application. This approach allows for dynamic configuration in different environments, such as development and production.

## Azure App Service Configuration

### Setting the Environment Variable in Azure

1. **Log into the Azure Portal**:
   - Navigate to [Azure Portal](https://portal.azure.com/) and sign in.

2. **Access Your App Service**:
   - Go to your App Service where the Blazor app is deployed.

3. **Open Application Settings**:
   - Find the "Settings" section in the left-hand navigation menu.
   - Click on "Configuration".

4. **Add New Application Setting**:
   - In the Configuration blade, locate the Application Settings section.
   - Click on "New application setting" or "+ Add".
   - Enter `Name` as `SQLFlowConStr` and `Value` as your actual connection string.
   - Click "OK" or "Save" to confirm.

5. **Restart the App Service**:
   - Restart your App Service after making changes to the Application Settings.

## Local Development Configuration

### Setting Environment Variables Locally

1. **Windows**:
   - Use the System Properties or set them via Command Prompt or PowerShell. Remember to restart your IDE after setting the environment variable.
   - Example in PowerShell:
     ```powershell
     [Environment]::SetEnvironmentVariable("SQLFlowConStr", "Your_Local_Connection_String", "User")
     ```
   - - Example in Command Prompt:
     ```powershell
     [Environment]::SetEnvironmentVariable("SQLFlowConStr", "Your_Local_Connection_String", "User")
     ```

2. **macOS/Linux**:
   - Set the environment variable in your shell profile (like `.bashrc` or `.bash_profile`).
   - Example in Terminal:
     ```bash
     export SQLFlowConStr=Your_Local_Connection_String
     ```

### Accessing the Environment Variable in the Application

The method to access the environment variable in your .NET Core application remains the same:

```csharp
var connectionString = Configuration.GetConnectionString("SQLFlowConStr");
```

## Best Practices

- Keep sensitive data out of your code repository.
- Use different settings for development, staging, and production environments.
- Regularly review and update your configuration and environment variables.
