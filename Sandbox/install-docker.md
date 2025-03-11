# Docker Desktop Installation Guide

A concise guide for downloading and installing Docker Desktop on your laptop.

## Prerequisites

### Windows
- Windows 10 64-bit: Home, Pro, Enterprise, or Education (Build 19041 or later)
- Windows 11 64-bit
- Enable hardware virtualization in BIOS/UEFI
- WSL 2 (Windows Subsystem for Linux) installed for Windows Home users

### macOS
- macOS 11 (Big Sur) or newer
- Apple Silicon (M1/M2) or Intel chip

### Linux
- Ubuntu 18.04 or newer, Debian 10 or newer, Fedora 32 or newer
- 64-bit kernel and CPU
- KVM virtualization support

## Step 1: Download Docker Desktop

### Windows
1. Go to [https://www.docker.com/products/docker-desktop/](https://www.docker.com/products/docker-desktop/)
2. Click "Download for Windows"
3. Save the Docker Desktop Installer.exe file

### macOS
1. Go to [https://www.docker.com/products/docker-desktop/](https://www.docker.com/products/docker-desktop/)
2. Click "Download for Mac"
3. Select either Apple Silicon or Intel Chip version as appropriate
4. Save the Docker.dmg file

### Linux
1. Go to [https://docs.docker.com/desktop/install/linux-install/](https://docs.docker.com/desktop/install/linux-install/)
2. Follow the instructions for your specific distribution (Ubuntu, Debian, Fedora)
3. Download the appropriate .deb or .rpm package

## Step 2: Install Docker Desktop

### Windows
1. Double-click Docker Desktop Installer.exe
2. Follow the installation wizard prompts
3. When prompted, select the option to use WSL 2 (recommended)
4. Click "Ok" to start the installation
5. Click "Close" when the installation is complete

### macOS
1. Double-click the Docker.dmg file
2. Drag the Docker icon to the Applications folder
3. Double-click the Docker icon in Applications to start Docker
4. Authorize Docker with your system password if prompted
5. Wait for Docker to start (indicated by the whale icon in the menu bar)

### Linux (Ubuntu/Debian example)
1. Open Terminal
2. Navigate to the download location
3. Install the package:
   ```
   sudo apt-get update
   sudo apt-get install ./docker-desktop-<version>-<arch>.deb
   ```
4. Start Docker Desktop:
   ```
   systemctl --user start docker-desktop
   ```

## Step 3: Verify Installation

1. Open a terminal or command prompt
2. Run the command:
   ```
   docker --version
   ```
3. Also verify Docker is working correctly:
   ```
   docker run hello-world
   ```
   This should download a test image and run it, confirming Docker is installed correctly.

## Step 4: Configure Docker Desktop (Optional)

1. Open Docker Desktop
2. Click the gear icon (Settings)
3. Configure resources (CPUs, Memory, Disk space) under "Resources"
4. Configure other settings as needed (File sharing, Network, etc.)
5. Click "Apply & Restart" to apply changes

## For Docker Desktop License

- Docker Desktop is free for:
  - Personal use
  - Small businesses (fewer than 250 employees AND less than $10 million in revenue)
  - Educational and non-commercial open source projects
- Larger organizations require a paid subscription

## Troubleshooting

### Windows
- If virtualization is not enabled, enable it in your BIOS settings
- For WSL 2 issues, run:
  ```
  wsl --update
  ```

### macOS
- If you encounter permission issues, check System Preferences > Security & Privacy
- For performance issues, adjust resource allocation in Docker Desktop settings

### Linux
- For permission issues, ensure your user is in the docker group:
  ```
  sudo usermod -aG docker $USER
  newgrp docker
  ```

## Additional Resources

- [Docker Desktop Documentation](https://docs.docker.com/desktop/)
- [Docker Get Started Guide](https://docs.docker.com/get-started/)
- [Docker Hub](https://hub.docker.com/) - for finding Docker images
- [Docker Desktop Release Notes](https://docs.docker.com/desktop/release-notes/)