# FlyShelf — Microsoft Store Build

This folder contains everything needed to build the Store version of FlyShelf.

## How It Works

The Store build uses the **exact same source code** as the standalone EXE.  
The only difference is the `MSIX_STORE` compile flag, which:

- ✅ Disables the auto-updater (Store manages updates)
- ✅ Disables terminal/sandbox code execution
- ✅ Disables Cloudflare tunnel subprocess
- ✅ Hides Pro upgrade payment redirects
- ✅ Hides System Logs tab (Release build)
- ✅ Packages as MSIX instead of standalone EXE

**No source code is copied or duplicated.** Everything is conditional.

## Folder Structure

```
MicrosoftBuild/
├── Package.appxmanifest    ← App identity, capabilities, startup task
├── Build_Store.bat         ← One-click build script
├── Assets/                 ← MSIX icon assets (18 PNGs at various sizes)
├── Output/                 ← Build output (.msix package goes here)
└── README.md               ← This file
```

## Building

1. Close FlyShelf if it's running
2. Open a terminal in `E:\exeapps\FlyShelf\MicrosoftBuild\`
3. Run: `.\Build_Store.bat`
4. Output goes to `MicrosoftBuild\Output\`

## Submitting to Partner Center

1. Go to https://partner.microsoft.com
2. Create a new app listing for "FlyShelf"
3. Fill in:
   - **Description**: Seamless cross-device clipboard sync
   - **Category**: Productivity > Utilities & Tools
   - **Privacy Policy URL**: https://shdra06.github.io/FlyShelf/privacy.html
   - **Support Contact**: your email
4. Upload the `.msix` or `.msixupload` from `Output/`
5. Complete the age rating questionnaire (IARC)
6. Submit for certification

## Important Notes

- The Store version is **Free tier only** — no Pro features, no payment redirects
- The `Publisher` in `Package.appxmanifest` must match your Partner Center certificate
- After creating your app in Partner Center, update the `Identity` fields in the manifest
- First time: use `AppxPackageSigningEnabled=false` → Partner Center signs it for you
