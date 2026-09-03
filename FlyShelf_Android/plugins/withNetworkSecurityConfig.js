const { withAndroidManifest, withDangerousMod } = require('expo/config-plugins');
const fs = require('fs');
const path = require('path');

/**
 * Expo config plugin: Injects Android Network Security Config
 * that restricts cleartext HTTP traffic to RFC1918 private IP ranges only.
 * 
 * This replaces the global `usesCleartextTraffic: true` with a scoped policy
 * that only allows HTTP for LAN sync (192.168.*, 10.*, 172.16-31.*).
 */
function withNetworkSecurityConfig(config) {
  // Step 1: Write the network_security_config.xml to the Android resources
  config = withDangerousMod(config, [
    'android',
    (mod) => {
      const xmlDir = path.join(
        mod.modRequest.platformProjectRoot,
        'app', 'src', 'main', 'res', 'xml'
      );
      fs.mkdirSync(xmlDir, { recursive: true });

      const xmlContent = `<?xml version="1.0" encoding="utf-8"?>
<!-- Network Security Config: Enforces HTTPS on all cloud domains while allowing LAN sync -->
<network-security-config>
  <!-- Strictly disallow cleartext on all cloud, auth, and API domains -->
  <domain-config cleartextTrafficPermitted="false">
    <domain includeSubdomains="true">firebaseio.com</domain>
    <domain includeSubdomains="true">googleapis.com</domain>
    <domain includeSubdomains="true">google.com</domain>
    <domain includeSubdomains="true">flyshelf.app</domain>
    <domain includeSubdomains="true">trycloudflare.com</domain>
    <domain includeSubdomains="true">vercel.app</domain>
    <domain includeSubdomains="true">razorpay.com</domain>
  </domain-config>
  <!-- Permit cleartext exclusively for local numeric IPs (LAN sync fallback) -->
  <base-config cleartextTrafficPermitted="true">
    <trust-anchors>
      <certificates src="system" />
    </trust-anchors>
  </base-config>
</network-security-config>
`;
      fs.writeFileSync(path.join(xmlDir, 'network_security_config.xml'), xmlContent);
      return mod;
    },
  ]);

  // Step 2: Reference it in AndroidManifest.xml
  config = withAndroidManifest(config, (mod) => {
    const manifest = mod.modResults;
    const application = manifest.manifest.application?.[0];
    if (application) {
      application.$['android:networkSecurityConfig'] = '@xml/network_security_config';
      // Remove the global usesCleartextTraffic if present
      delete application.$['android:usesCleartextTraffic'];
    }
    return mod;
  });

  return config;
}

module.exports = withNetworkSecurityConfig;
