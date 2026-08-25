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
<!-- Network Security Config: Restricts cleartext HTTP to LAN sync only -->
<network-security-config>
  <!-- Default: block all cleartext traffic -->
  <base-config cleartextTrafficPermitted="false">
    <trust-anchors>
      <certificates src="system" />
    </trust-anchors>
  </base-config>
  <!-- Allow cleartext for LAN sync (RFC1918 private ranges) -->
  <domain-config cleartextTrafficPermitted="true">
    <domain includeSubdomains="true">10.0.0.0</domain>
    <domain includeSubdomains="true">172.16.0.0</domain>
    <domain includeSubdomains="true">192.168.0.0</domain>
    <domain includeSubdomains="false">localhost</domain>
    <domain includeSubdomains="false">127.0.0.1</domain>
    <domain includeSubdomains="false">10.0.2.2</domain>
  </domain-config>
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
