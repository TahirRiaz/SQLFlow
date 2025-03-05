
# Enabling WebSockets in Azure App Service

This guide will walk you through the steps to enable WebSockets in an Azure App Service. WebSockets provide a full-duplex communication channel over a single, long-lived connection, making them ideal for real-time applications.

## Prerequisites

- An Azure subscription. If you don't have one, you can create a free account at [Azure Free Trial](https://azure.microsoft.com/en-us/free/).
- An existing Azure App Service. If you need to create one, follow the instructions at [Create an Azure App Service](https://docs.microsoft.com/en-us/azure/app-service/quickstart-arm-template).

## Step-by-Step Guide

### Step 1: Navigate to Your App Service

1. Log in to the Azure Portal at [https://portal.azure.com](https://portal.azure.com).
2. Navigate to **App Services** and select the App Service you want to configure.

### Step 2: Open Application Settings

1. In the menu on the left, scroll down to the **Settings** group.
2. Click on **Configuration** to open the application settings.

### Step 3: Enable WebSockets

1. In the **Configuration** blade, find the **General settings** tab.
2. Locate the **WebSockets** option and toggle it to **On**.

![Enable WebSockets in Azure App Service](your-image-url-here)  # Replace with an actual image if needed

### Step 4: Save and Restart

1. Click the **Save** button at the top of the **Configuration** blade to apply your changes.
2. Restart your App Service to ensure the changes take effect. You can do this by navigating back to the overview of your App Service and clicking **Restart**.

## Verification

To verify that WebSockets are enabled, you can use a WebSocket client to connect to your app. If the connection is successful, WebSockets are enabled and working.


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
     ```
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
