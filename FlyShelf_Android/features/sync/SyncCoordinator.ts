/**
 * SyncCoordinator — Serializes sync operations to prevent race conditions.
 * Uses a priority queue with mutex-style locking.
 * 
 * Fixes: C5 (race conditions in concurrent sync operations)
 */

export type SyncPriority = 'CRITICAL' | 'HIGH' | 'NORMAL' | 'LOW';

interface QueuedOperation {
  id: string;
  priority: SyncPriority;
  execute: () => Promise<void>;
  resolve: () => void;
  reject: (err: any) => void;
  enqueuedAt: number;
}

const PRIORITY_ORDER: Record<SyncPriority, number> = {
  CRITICAL: 0,
  HIGH: 1,
  NORMAL: 2,
  LOW: 3,
};

class SyncCoordinatorImpl {
  private queue: QueuedOperation[] = [];
  private isProcessing = false;
  private activeOpId: string | null = null;

  /**
   * Enqueue a sync operation with a given priority.
   * Returns a promise that resolves when the operation completes.
   */
  async enqueue(
    id: string,
    operation: () => Promise<void>,
    priority: SyncPriority = 'NORMAL'
  ): Promise<void> {
    // Deduplicate: if same id is already queued (not running), skip
    if (this.queue.some(op => op.id === id)) {
      return;
    }

    return new Promise<void>((resolve, reject) => {
      this.queue.push({
        id,
        priority,
        execute: operation,
        resolve,
        reject,
        enqueuedAt: Date.now(),
      });

      // Sort by priority
      this.queue.sort((a, b) => PRIORITY_ORDER[a.priority] - PRIORITY_ORDER[b.priority]);

      this.processNext();
    });
  }

  /**
   * Check if a specific operation is currently running.
   */
  isRunning(id: string): boolean {
    return this.activeOpId === id;
  }

  /**
   * Check if a specific operation is queued (waiting).
   */
  isQueued(id: string): boolean {
    return this.queue.some(op => op.id === id);
  }

  /**
   * Get current queue status for debugging.
   */
  getStatus(): { activeOp: string | null; queueLength: number; queuedIds: string[] } {
    return {
      activeOp: this.activeOpId,
      queueLength: this.queue.length,
      queuedIds: this.queue.map(op => op.id),
    };
  }

  private async processNext(): Promise<void> {
    if (this.isProcessing || this.queue.length === 0) return;

    this.isProcessing = true;
    const op = this.queue.shift()!;
    this.activeOpId = op.id;

    try {
      await op.execute();
      op.resolve();
    } catch (err) {
      op.reject(err);
    } finally {
      this.activeOpId = null;
      // C-3 FIX: Process next BEFORE clearing isProcessing to prevent
      // re-entrant execution from a concurrent enqueue() call.
      if (this.queue.length > 0) {
        // isProcessing stays true → no re-entry from enqueue()
        const next = this.queue.shift()!;
        this.activeOpId = next.id;
        try {
          await next.execute();
          next.resolve();
        } catch (nextErr) {
          next.reject(nextErr);
        }
        // Continue draining
        this.activeOpId = null;
        if (this.queue.length > 0) {
          // Use queueMicrotask to avoid deep recursion stack
          queueMicrotask(() => this.processNext());
        } else {
          this.isProcessing = false;
        }
      } else {
        this.isProcessing = false;
      }
    }
  }
}

/** Singleton instance */
export const SyncCoordinator = new SyncCoordinatorImpl();
