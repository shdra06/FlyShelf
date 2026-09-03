import re
path = 'e:/exeapps/FlyShelf/FlyShelf_Android/hooks/usePcUrlResolver.ts'
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

# Fix M-3
inv_old = '''  const invalidateCache = useCallback(() => {
    cachedPcUrlRef.current = null;
    cachedPcUrlTimestampRef.current = 0;
  }, []);'''
  
inv_new = '''  const invalidateCache = useCallback(() => {
    cachedPcUrlRef.current = null;
    cachedPcUrlTimestampRef.current = 0;
    activeUrlResolutionPromiseRef.current = null;
  }, []);'''
content = content.replace(inv_old, inv_new)

# Fix M-2 and M-6
lan_old = '''      if (uniqueLan.length > 0) {
        syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] 🔍 Probing ${uniqueLan.length} LAN candidate(s)...`);
        try {
          const lanWinner = await Promise.any(uniqueLan.map(url => probeUrl(url, 1500)));
          if (lanWinner) {
            cachedPcUrlRef.current = lanWinner;
            cachedPcUrlTimestampRef.current = startNow;
            discoveryMethodRef.current = 'stored-lan';
            AsyncStorage.setItem('@flyshelf_last_lan_url', lanWinner).catch(() => {});
            try {
              const urlObj = new URL(lanWinner);
              addToPcIpCache(urlObj.hostname, parseInt(urlObj.port) || 8999).catch(() => {});
            } catch {}
            syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] 🚀 LAN Connected in ${NetworkClock.now() - startNow}ms: ${lanWinner}`);
            return lanWinner;
          }
        } catch {
          syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ⚠️ All LAN probes failed`);
        }
      }'''

lan_new = '''      if (uniqueLan.length > 0) {
        syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] 🔍 Probing ${uniqueLan.length} LAN candidate(s)...`);
        try {
          const BATCH_SIZE = 10;
          for (let i = 0; i < uniqueLan.length; i += BATCH_SIZE) {
            const batch = uniqueLan.slice(i, i + BATCH_SIZE);
            const controller = new AbortController();
            try {
              const results = await Promise.allSettled(batch.map(url => probeUrl(url, 1500, controller.signal)));
              const found = results.find((r: any) => r.status === 'fulfilled');
              if (found) {
                controller.abort();
                const lanWinner = (found as PromiseFulfilledResult<string>).value;
                cachedPcUrlRef.current = lanWinner;
                cachedPcUrlTimestampRef.current = startNow;
                discoveryMethodRef.current = 'stored-lan';
                AsyncStorage.setItem('@flyshelf_last_lan_url', lanWinner).catch(() => {});
                try {
                  const urlObj = new URL(lanWinner);
                  addToPcIpCache(urlObj.hostname, parseInt(urlObj.port) || 8999).catch(() => {});
                } catch {}
                syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] 🚀 LAN Connected in ${NetworkClock.now() - startNow}ms: ${lanWinner}`);
                return lanWinner;
              }
            } finally {
              controller.abort();
            }
          }
        } catch {
          syncLog('URL-RESOLVE', `[STEP 3/6: URL RESOLVE] ⚠️ All LAN probes failed`);
        }
      }'''

probe_old = '''      const probeUrl = async (url: string, timeout = 2500): Promise<string> => {
        try {
          const res = await fetchWithTimeout(`${url}/api/health`, { headers: probeHeaders }, timeout);'''

probe_new = '''      const probeUrl = async (url: string, timeout = 2500, signal?: AbortSignal): Promise<string> => {
        try {
          const res = await fetchWithTimeout(`${url}/api/health`, { headers: probeHeaders, signal }, timeout);'''

content = content.replace(probe_old, probe_new)
content = content.replace(lan_old, lan_new)

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
print('Done')
