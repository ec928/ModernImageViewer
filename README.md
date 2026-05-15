🚀 Modern Image Viewer (Public Beta)
A high-performance, hardware-accelerated image viewer built for creators, 3D artists, and power users. Designed specifically to handle massive directories of high-resolution images with zero UI lag and unique windowing capabilities.

✨ Key Features
Tear-Off Viewports: Instantly detach any image into its own borderless, floating window - ideal for secondary monitors or overlaying reference images while working in Blender, Photoshop, or AI tools.

GPU-Accelerated Rendering: Powered by Win2D (Direct2D) for near-instant rendering and smooth scaling, offloading heavy image decoding to the graphics card.

Massive Folder Support: Tiered-decoding architecture allows for seamless browsing of folders containing thousands of high-res (10MB+) images without stuttering or memory leaks.

Native Windows 11 Design: Built using the Windows App SDK (WinUI 3) for a clean, modern aesthetic with Mica material and native OS performance.

🛠 Technical Stack
Framework: C# / .NET 8

UI Engine: WinUI 3 (Windows App SDK)

Graphics: Win2D (Direct2D)

Architecture: Asynchronous tiered decoding and manual COM reference management for VRAM efficiency.

📦 Installation (Portable)
This app is distributed as a Self-Contained Portable App. No installation or registry changes are required.

Download the latest .zip from the [Releases](https://github.com/ec928/ModernImageViewer/releases) page.

Extract the folder to your preferred location.

Run ModernImageViewer.exe.

Note on Windows SmartScreen: Because this is an unsigned indie beta, Windows Defender may flag it. Click More info -> Run anyway to launch.  This is standard for indie software that hasn't been "signed" with an expensive corporate certificate.  You can verify the safety of the application by reviewing the source code provided in this repository. 

💬 Feedback
This is an active public beta. If you encounter bugs, performance issues, or have feature requests, please open a ticket in the Issues tab.
