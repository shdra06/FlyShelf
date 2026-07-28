const { firebaseFetch } = require('./_firebaseAdmin');

/**
 * Prunes expired rate limit records from Firebase RTDB.
 * Call periodically (e.g., daily via Vercel Cron) to prevent unbounded growth.
 * 
 * Records older than 24 hours are deleted.
 */
async function pruneRateLimits() {
  const dbUrl = process.env.FIREBASE_RTDB_URL;
  if (!dbUrl) return { error: 'FIREBASE_RTDB_URL not configured' };

  const cutoff = Date.now() - 86400000; // 24 hours ago
  const paths = ['verifyPayment', 'createOrder', 'activate', 'recovery'];
  let totalPruned = 0;

  for (const path of paths) {
    try {
      const res = await firebaseFetch(`${dbUrl}/rate_limits/${path}.json?shallow=true`);
      if (!res.ok) continue;
      const ipHashes = await res.json();
      if (!ipHashes) continue;

      for (const ipHash of Object.keys(ipHashes)) {
        const entriesRes = await firebaseFetch(`${dbUrl}/rate_limits/${path}/${ipHash}.json`);
        if (!entriesRes.ok) continue;
        const entries = await entriesRes.json();
        if (!entries) continue;

        for (const [ts, val] of Object.entries(entries)) {
          const entryTime = new Date(val).getTime();
          if (isNaN(entryTime) || entryTime < cutoff) {
            await firebaseFetch(`${dbUrl}/rate_limits/${path}/${ipHash}/${ts}.json`, {
              method: 'DELETE'
            });
            totalPruned++;
          }
        }

        // If all entries deleted, remove the IP hash node
        const recheck = await firebaseFetch(`${dbUrl}/rate_limits/${path}/${ipHash}.json`);
        if (recheck.ok) {
          const remaining = await recheck.json();
          if (!remaining || Object.keys(remaining).length === 0) {
            await firebaseFetch(`${dbUrl}/rate_limits/${path}/${ipHash}.json`, { method: 'DELETE' });
          }
        }
      }
    } catch (e) {
      console.error(`[cleanup] Error pruning ${path}:`, e.message);
    }
  }

  return { pruned: totalPruned };
}

module.exports = { pruneRateLimits };
