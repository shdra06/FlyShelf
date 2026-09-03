const { withAndroidManifest } = require('expo/config-plugins');

/**
 * Expo config plugin: Registers FlyShelf as an Android Share Target.
 *
 * Adds intent-filters to the main activity so FlyShelf appears in the
 * Android share sheet when users share files/text from other apps.
 * 
 * Supports:
 *   - ACTION_SEND (single file/text)
 *   - ACTION_SEND_MULTIPLE (multiple files)
 *   - All common MIME types: images, PDFs, documents, videos, text, any file
 */
function withShareTarget(config) {
  config = withAndroidManifest(config, (mod) => {
    const manifest = mod.modResults;
    const mainActivity = manifest.manifest.application?.[0]?.activity?.find(
      (a) => a.$['android:name'] === '.MainActivity'
    );

    if (!mainActivity) {
      console.warn('[withShareTarget] Could not find .MainActivity in AndroidManifest.xml');
      return mod;
    }

    // Ensure intent-filter array exists
    if (!mainActivity['intent-filter']) {
      mainActivity['intent-filter'] = [];
    }

    // ── Single file share (ACTION_SEND) ──
    mainActivity['intent-filter'].push({
      action: [{ $: { 'android:name': 'android.intent.action.SEND' } }],
      category: [{ $: { 'android:name': 'android.intent.category.DEFAULT' } }],
      data: [
        { $: { 'android:mimeType': 'image/*' } },
        { $: { 'android:mimeType': 'video/*' } },
        { $: { 'android:mimeType': 'application/pdf' } },
        { $: { 'android:mimeType': 'application/msword' } },
        { $: { 'android:mimeType': 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' } },
        { $: { 'android:mimeType': 'application/vnd.ms-excel' } },
        { $: { 'android:mimeType': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' } },
        { $: { 'android:mimeType': 'application/vnd.ms-powerpoint' } },
        { $: { 'android:mimeType': 'application/vnd.openxmlformats-officedocument.presentationml.presentation' } },
        { $: { 'android:mimeType': 'text/*' } },
        { $: { 'android:mimeType': 'application/zip' } },
        { $: { 'android:mimeType': 'application/*' } },
      ],
    });

    // ── Multiple files share (ACTION_SEND_MULTIPLE) ──
    mainActivity['intent-filter'].push({
      action: [{ $: { 'android:name': 'android.intent.action.SEND_MULTIPLE' } }],
      category: [{ $: { 'android:name': 'android.intent.category.DEFAULT' } }],
      data: [
        { $: { 'android:mimeType': 'image/*' } },
        { $: { 'android:mimeType': 'video/*' } },
        { $: { 'android:mimeType': 'application/*' } },
        { $: { 'android:mimeType': 'text/*' } },
      ],
    });

    return mod;
  });

  return config;
}

module.exports = withShareTarget;
