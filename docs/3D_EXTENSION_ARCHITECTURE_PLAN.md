# 3D Parallax Extension Architecture Plan (Approach 2: Fully Decoupled Plugin)

## 1. Executive Summary & Architectural Motivation

Modern Image Viewer's core architectural identity is:
- **Instantaneous launch** (~1.5s cold start for loose files).
- **Lightweight resource footprint** (~25 MB total portable folder).
- **Purity**: Zero background processes, zero telemetry, zero unexpected network calls, and universal compatibility across all Windows 10/11 machines regardless of GPU capability.

The 3D Parallax feature (depth estimation via Depth Anything V2 Small and salient subject cutout via BiRefNet-Lite) is highly compelling for single-photo scenes in Multi-View shows. However, embedding it directly into the base distribution creates severe architectural conflict:
1. **10x File Bloat**: Bundling the two ONNX models (`depth-anything-v2-small.onnx` at ~94 MB and `birefnet-lite.onnx` at ~109 MB) plus native runtime dependencies (`onnxruntime.dll` at 21.6 MB and `DirectML.dll` at 18.7 MB) expands the base app from **~25 MB to over 270 MB**.
2. **GPU & Driver Overhead**: DirectML hardware acceleration requires a capable DirectX 12 GPU. Low-power ultrabooks, virtual machines, and basic enterprise laptops can experience driver hangs, high memory pressure, or battery drain.
3. **Distribution Cleanliness**: Users who simply want a fast image viewer should never pay the storage, memory, or bandwidth cost of computer vision models.

**Decision**: Implement **Approach 2 (Fully Decoupled Plugin)**. The base application ships 100% clean of all ML binaries and models (~25 MB). 3D capability is delivered as an opt-in, one-click extension managed from the settings menu, following the proven model from **TypoZen**.

---

## 2. The TypoZen Opt-In Extension Paradigm

In TypoZen (`TypoZen/Extensions.cs`), large optional assets (Kokoro TTS, Whisper speech models, Wiktionary databases) adhere to strict principles:

1. **Zero Network Calls on Launch**: The application never checks remote servers or reaches out to the network unless the user explicitly clicks "Install" on an extension.
2. **Declarative Catalog & Checksums**: Each extension declares its remote URLs, target file paths, expected sizes, and SHA-256 hashes.
3. **Dedicated Isolated Directory**: All extension assets reside in:
   `%LOCALAPPDATA%\ModernImageViewer\Extensions\3D\`
4. **Clean One-Click Removal**: Uninstalling an extension unloads the runtime context, verifies the folder is not a reparse point, executes a multi-pass wipe of all extension assets, renames any locked native DLLs to `*.pending-delete` for automatic cleanup on next process start, and verifies that no active assets remain. On launch, any surviving temporary or orphaned extension files are swept.
5. **Self-Hiding UI Affordances**: When an extension is not installed, menus and presets that depend on it either gracefully hide or provide a clean prompt directing the user to the Extensions manager.

---

## 3. Core & Extension Decoupling Architecture

```
┌────────────────────────────────────────────────────────────────────────┐
│  Modern Image Viewer Core (Base App ~25 MB)                            │
│                                                                        │
│  • MainWindow (Header Settings Wheel: Extensions…)                     │
│  • MultiViewControl (Show card, presets, camera engine)                │
│  • ExtensionManager (Download, progress, hash check, uninstall)        │
│  • IParallaxService (Core Interface Contract)                          │
│                                                                        │
│       │                                                                │
│       │ (If installed, loaded via ParallaxLoadContext)                 │
│       ▼                                                                │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │ %LOCALAPPDATA%\ModernImageViewer\Extensions\3D\ (248 MB)         │  │
│  │                                                                  │  │
│  │ • ModernImageViewer.Extensions.Parallax.dll                      │  │
│  │ • Microsoft.ML.OnnxRuntime.dll                                   │  │
│  │ • onnxruntime.dll (21.6 MB)                                      │  │
│  │ • DirectML.dll (18.7 MB)                                         │  │
│  │ • Models/depth-anything-v2-small.onnx (94 MB)                    │  │
│  │ • Models/birefnet-lite.onnx (109 MB)                             │  │
│  └──────────────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────────────┘
```

### 3.1 The Core Contract Interface (`IParallaxService`)
The base application defines the interface contract in the main project without any dependency on ONNX or DirectML:

```csharp
namespace ModernImageViewer.Contracts
{
    public interface IParallaxService
    {
        bool IsAvailable { get; }
        Task<ParallaxDepthMap?> GetDepthMapAsync(string photoPath);
        Task<ParallaxSubjectMask?> GetSubjectMaskAsync(string photoPath);
        ParallaxFitVerdict EvaluateSuitability(string photoPath, ParallaxDepthMap depth, ParallaxSubjectMask? mask);
        void DisposeSession();
    }
}
```

### 3.2 Dynamic Assembly Isolation (`ParallaxLoadContext`)
When the 3D Extension is installed, a specialized `AssemblyLoadContext` dynamically loads the extension DLL and resolves its private dependencies:

```csharp
public sealed class ParallaxLoadContext : AssemblyLoadContext
{
    private readonly string _extensionDir;
    private readonly AssemblyDependencyResolver _resolver;

    public ParallaxLoadContext(string extensionDir) : base(isCollectible: true)
    {
        _extensionDir = extensionDir;
        string mainAssembly = Path.Combine(extensionDir, "ModernImageViewer.Extensions.Parallax.dll");
        _resolver = new AssemblyDependencyResolver(mainAssembly);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // Resolves onnxruntime.dll and DirectML.dll directly from the extension directory
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path != null) return LoadUnmanagedDllFromPath(path);

        string localDll = Path.Combine(_extensionDir, unmanagedDllName + ".dll");
        if (File.Exists(localDll)) return LoadUnmanagedDllFromPath(localDll);

        return base.LoadUnmanagedDll(unmanagedDllName);
    }
}
```

### 3.3 Compile-Time Purity in the Base Project
- The base `ModernImageViewer.csproj` does **not** reference NuGet package `Microsoft.ML.OnnxRuntime.DirectML`.
- The target `RemoveUnusedWindowsML` in `ModernImageViewer.csproj` continues to strip all Windows ML / AI assemblies on publish, preserving the ~25 MB base size.
- A separate lightweight project `ModernImageViewer.Extensions.Parallax.csproj` compiles the 3D engine and output files into a distributable bundle.

---

## 4. User Experience & UI Workflow

### 4.1 Entry Point: Settings Wheel (`HeaderSettingsFlyout`)
In `MainWindow.xaml`, inside the header settings flyout (the top-right gear icon ``), an **"Extensions…"** option sits prominently between the tools and the "About" button:

```xaml
<Rectangle Height="1" Fill="{ThemeResource DividerStrokeColorDefaultBrush}" Opacity="0.5"/>
<Button Style="{StaticResource SubtleButtonStyle}" Click="Extensions_Click"
        HorizontalAlignment="Stretch" HorizontalContentAlignment="Left" Height="28" Padding="8,0" CornerRadius="4">
    <StackPanel Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
        <FontIcon Glyph="&#xE74C;" FontSize="13.5" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
        <TextBlock Text="Extensions…" FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/>
    </StackPanel>
</Button>
<Button Style="{StaticResource SubtleButtonStyle}" Click="About_Click" ... />
```

### 4.2 The Extensions Dialog / Flyout
Clicking **Extensions…** displays the Extension Manager:

1. **Card Header**:
   - Title: **3D Motion (Slide & Sway)**
   - Description: *AI depth maps and salient subject segmentation for cinematic single-picture parallax in Multi-View shows. Requires a DirectX 12 capable GPU.*
   - Disk Requirement: *~248 MB total (models & GPU runtime).*
2. **Dynamic Status States**:
   - **State A (Not Installed)**:
     - Badge: `Not installed`
     - Button: `[ Install Extension ]`
   - **State B (Downloading / Installing)**:
     - Shows a WinUI 3 `ProgressBar` with transferred MB, percentage, and download speed.
     - Button: `[ Cancel ]`
     - Uses standard non-blocking `HttpClient` streaming with temporary `.download` files.
   - **State C (Installed)**:
     - Badge: `Installed (248.5 MB on disk)`
     - Button: `[ Remove Extension ]`
     - Reclaims disk space and unloads the plugin immediately.

### 4.3 Multi-View Show Card Adaptation
- **When Extension is NOT Installed**:
  - The **3D** preset button on the Show card shows a subtle lock or download indicator ``.
  - Clicking "3D" displays a friendly toast with a one-click button: *"3D requires the 3D Extension. [Open Extensions]"*.
  - The Camera settings panel hides the 3D depth sliders, avoiding clutter.
- **When Extension IS Installed**:
  - The **3D** preset activates with 100% functionality.
  - "Slide" and "Sway" motion chips are available.
  - Background asynchronous depth and mask generation runs as designed.

---

## 5. Asset Packaging & Distribution Specification

The 3D Extension bundle contains five files totaling **~248 MB**:

| File | Type | Source | Size |
|---|---|---|---|
| `ModernImageViewer.Extensions.Parallax.dll` | Managed Plugin | Compiled from repo | ~80 KB |
| `Microsoft.ML.OnnxRuntime.dll` | Managed Wrapper | NuGet (v1.20+) | ~800 KB |
| `onnxruntime.dll` | Native DirectML Runtime | Microsoft ONNX Runtime | ~21.6 MB |
| `DirectML.dll` | Native GPU Backend | Microsoft DirectML | ~18.7 MB |
| `Models/depth-anything-v2-small.onnx` | Model (Apache-2.0) | HuggingFace `onnx-community` | ~94.5 MB |
| `Models/birefnet-lite.onnx` | Model (MIT) | HuggingFace `onnx-community` | ~109.2 MB |

### Delivery & Packaging Architecture:
1. **Automated Bundle Publishing**: `publish.bat` publishes `ModernImageViewer.Extensions.Parallax.csproj` into `bin\Extensions\Parallax\bundle\` containing the managed plugin DLL, `Microsoft.ML.OnnxRuntime.dll`, `onnxruntime.dll`, and `DirectML.dll`.
2. **Model Retrieval & Verification**: On user request, models stream from verified HuggingFace endpoints with non-blocking progress reporting, atomic `.tmp` download writes, 20-minute connection timeouts, and file size / SHA-256 verification before placement.
3. **Resilient Multi-Pass Wipe**: `Uninstall3D` unloads the collectible context, verifies the target is not a reparse point, performs a 3-pass file deletion, renames locked runtime DLLs to `*.pending-delete`, and verifies the directory is either completely deleted or contains strictly `*.pending-delete` files. Startup sweeps clean up any pending-delete or orphaned files.

---

## 6. Implementation Plan & Milestones

### Phase 1: Contracts & Project Separation
- Create `ModernImageViewer.Contracts` with `IParallaxService` and related DTO records (`ParallaxDepthMap`, `ParallaxSubjectMask`, `ParallaxVerdict`).
- Create standalone project `ModernImageViewer.Extensions.Parallax.csproj` moving `DepthMaps.cs`, `SubjectMasks.cs`, `ParallaxLayers.cs`, and `ParallaxJudge.cs` into it.
- Remove ONNX Runtime dependencies and references from `ModernImageViewer.csproj`.

### Phase 2: Dynamic Load Context & Service Locator
- Implement `ParallaxLoadContext` in core.
- Implement `ParallaxPluginHost` which checks if `%LOCALAPPDATA%\ModernImageViewer\Extensions\3D\` exists and contains valid assemblies.
- Provide seamless fallback to a dummy `NoOpParallaxService` when uninstalled.

### Phase 3: Extension Manager Backend
- Implement `ExtensionManager.cs`:
  - `DownloadExtensionAsync(IProgress<double> progress, CancellationToken ct)`
  - `UninstallExtensionAsync()`
  - `IsExtensionInstalled()`
  - `GetExtensionDiskSize()`
- Integrate SHA-256 verification and atomic extraction (`.tmp` -> rename).

### Phase 4: UI & Settings Integration
- Add "Extensions…" item to `HeaderSettingsFlyout` in `MainWindow.xaml`.
- Build `ExtensionsDialog` (Fluent WinUI 3 dialog with progress, status, and install/remove buttons).
- Wire Show card presets to check `ParallaxPluginHost.IsInstalled`.

### Phase 5: Verification & Gate
- Verify base release build size is ~25 MB (zero ML DLLs in `bin\`).
- Verify clean cold start without extension.
- Perform end-to-end install test: click Install -> download -> 3D becomes instantly active.
- Perform end-to-end remove test: click Remove -> 248 MB deleted -> 3D presets revert gracefully without crash or restart.
