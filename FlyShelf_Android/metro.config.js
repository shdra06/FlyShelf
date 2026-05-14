// Learn more https://docs.expo.io/guides/customizing-metro
const { getDefaultConfig } = require('expo/metro-config');
const path = require('path');
const fs = require('fs');

/** @type {import('expo/metro-config').MetroConfig} */
const config = getDefaultConfig(__dirname);

// Fix: Metro's FallbackWatcher crashes on Windows when it encounters 
// broken symlinks/missing dirs in expo-module-gradle-plugin/bin/.gradle
// Delete these dirs at Metro startup since they're Gradle build artifacts
const brokenDir = path.join(
  __dirname, 'node_modules', 'expo-modules-core',
  'expo-module-gradle-plugin', 'bin'
);

// Remove Gradle artifacts that cause watcher crashes
['.gradle', 'build', 'src'].forEach(sub => {
  const p = path.join(brokenDir, sub);
  try { fs.rmSync(p, { recursive: true, force: true }); } catch {}
});

module.exports = config;
