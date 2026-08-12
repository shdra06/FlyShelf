const { firebaseFetch } = require('./_firebaseAdmin');

// ═══════════════════════════════════════════════════════════════════
// Rate Limit Cleanup (v2.0.0)
// Prunes expired rate limit records from Firebase RTDB.
// Runs as a Vercel Cron Job (daily at 3 AM UTC) or can be called manually.
// Records older than 24 hours are deleted.
// ═══════════════════════════════════════════════════════════════════

// Also prune rate limits from the FlyShelf device tracking DB
const FLYSHELF_DB_URL = process.env.FLYSHELF_RTDB_URL;
const FLYSHELF_DB_SECRET = process.env.FLYSHELF_DB_SECRET;

async function flyshelfFetch(url, options = {}) {
  if (FLYSHELF_DB_SECRET) {
    const separator = url.includes('?') ? '&' : '?';
    url = `${url}${separator}auth=${encodeURIComponent(FLYSHELF_DB_SECRET)}`;
  }
  return fetch(url, options);
}

async function pruneDb(dbUrl, fetchFn, paths, label) {
  const cutoff = Date.now() - 86400000; // 24 hours ago
  let totalPruned = 0;

  for (const path of paths) {
    try {
      const res = await fetchFn(`${dbUrl}/rate_limits/${path}.json?shallow=true`);
      if (!res.ok) continue;
      const ipHashes = await res.json();
      if (!ipHashes) continue;

      for (const ipHash of Object.keys(ipHashes)) {
        const entriesRes = await fetchFn(`${dbUrl}/rate_limits/${path}/${ipHash}.json`);
        if (!entriesRes.ok) continue;
        const entries = await entriesRes.json();
        if (!entries) continue;

        for (const [ts, val] of Object.entries(entries)) {
          const entryTime = new Date(val).getTime();
          if (isNaN(entryTime) || entryTime < cutoff) {
            await fetchFn(`${dbUrl}/rate_limits/${path}/${ipHash}/${ts}.json`, {
              method: 'DELETE'
            });
            totalPruned++;
          }
        }

        // If all entries deleted, remove the IP hash node
        const recheck = await fetchFn(`${dbUrl}/rate_limits/${path}/${ipHash}.json`);
        if (recheck.ok) {
          const remaining = await recheck.json();
          if (!remaining || Object.keys(remaining).length === 0) {
            await fetchFn(`${dbUrl}/rate_limits/${path}/${ipHash}.json`, { method: 'DELETE' });
          }
        }
      }
    } catch (e) {
      console.error(`[cleanup:${label}] Error pruning ${path}:`, e.message);
    }
  }

  return totalPruned;
}

async function pruneRateLimits() {
  const dbUrl = process.env.FIREBASE_RTDB_URL;
  let totalPruned = 0;

  // Prune advance-sync RTDB rate limits
  if (dbUrl) {
    const count = await pruneDb(dbUrl, firebaseFetch, 
      ['verifyPayment', 'createOrder', 'activate', 'recovery', 'revalidate'], 
      'advance-sync'
    );
    totalPruned += count;
    console.log(`[cleanup] advance-sync: pruned ${count} expired entries`);
  }

  // Prune flyshelf-1c8d2 RTDB rate limits
  if (FLYSHELF_DB_URL && FLYSHELF_DB_SECRET) {
    const count = await pruneDb(FLYSHELF_DB_URL, flyshelfFetch, 
      ['register'], 
      'flyshelf'
    );
    totalPruned += count;
    console.log(`[cleanup] flyshelf: pruned ${count} expired entries`);
  }

  return { pruned: totalPruned };
}

// ═══ Vercel Cron Handler ═══
// Vercel cron triggers GET requests to this endpoint
module.exports = async function handler(req, res) {
  // Only allow GET (Vercel Cron) or POST (manual trigger)
  if (req.method !== 'GET' && req.method !== 'POST') {
    return res.status(405).json({ error: 'Method Not Allowed' });
  }

  // Verify cron secret to prevent unauthorized triggers
  const cronSecret = process.env.CRON_SECRET;
  if (cronSecret) {
    const authHeader = req.headers.authorization;
    if (authHeader !== `Bearer ${cronSecret}`) {
      return res.status(401).json({ error: 'Unauthorized' });
    }
  }

  try {
    const result = await pruneRateLimits();
    console.log(`[cleanup] ✅ Total pruned: ${result.pruned}`);
    return res.status(200).json({ success: true, ...result });
  } catch (err) {
    console.error('[cleanup] Error:', err);
    return res.status(500).json({ error: 'Cleanup failed.' });
  }
};

// Also export for direct use
module.exports.pruneRateLimits = pruneRateLimits;
