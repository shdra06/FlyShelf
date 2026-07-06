/**
 * Shared TypeScript types for Notes & Todos — mirrors PC's C# data models.
 * Used by notes.tsx, todo.tsx, and sync utilities.
 */

// ═══════════════════════════════════════════════════════════
// NOTES TYPES
// ═══════════════════════════════════════════════════════════

export type SubBulletItem = {
  Id: string;
  Text: string;
  IsDone: boolean;
};

export type NoteBullet = {
  Id: string;
  Header: string;
  Content: string;
  IsCollapsed: boolean;
  ImageDisplayWidth: number;
  ImageDisplayWidth2: number;
  CreatedAt: string;
  LastEdited: string;
  Tags: string[];
  Color: string;
  IsPinned: boolean;
  SortOrder: number;
  SubBullets: SubBulletItem[];
  CreatedByDevice?: string;
  LastEditedByDevice?: string;
};

export type FreeformSection = {
  Id: string;
  Title?: string;
  Content: string;
  CreatedAt: string;
};

export type NoteDay = {
  Date: string;
  IsFreeformMode: boolean;
  Bullets: NoteBullet[];
  FreeformSections: FreeformSection[];
  FreeformContent?: string;
  LastModified?: number;
};

// ═══════════════════════════════════════════════════════════
// TODO TYPES
// ═══════════════════════════════════════════════════════════

/** None=0, Low=1, Medium=2, High=3 */
export type TodoPriority = 0 | 1 | 2 | 3;

/** None=0, Daily=1, Weekly=2, Monthly=3 */
export type TodoRecurrence = 0 | 1 | 2 | 3;

export const PriorityLabels: Record<TodoPriority, string> = {
  0: '',
  1: '!',
  2: '!!',
  3: '!!!',
};

export const PriorityColors: Record<TodoPriority, string> = {
  0: '#666680',
  1: '#22C55E',
  2: '#F59E0B',
  3: '#FF4444',
};

export const RecurrenceLabels: Record<TodoRecurrence, string> = {
  0: '',
  1: '🔄 Daily',
  2: '🔄 Weekly',
  3: '🔄 Monthly',
};

export type TodoItem = {
  Id: string;
  Text: string;
  IsDone: boolean;
  CreatedAt: string;
  LastEdited?: string;
  Priority: TodoPriority;
  DueDate?: string | null;
  Tags: string[];
  Color: string;
  Description: string;
  SubTasks: TodoItem[]; // NOTE: SubTasks are recursive but UI should enforce max depth (recommended: 3 levels)
  Recurrence: TodoRecurrence;
  SortOrder: number;
  TimerMinutes?: number | null;
  ReminderAt?: string | null;
  CreatedByDevice?: string;
  LastEditedByDevice?: string;
};

export type TodoDay = {
  Date: string;
  Items: TodoItem[];
  LastModified?: number;
};

// ═══════════════════════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════════════════════

/** Generate a 16-char hex ID (8 bytes) for better collision resistance */
export const generateId = (): string => {
  const bytes = new Uint8Array(8);
  if (typeof crypto !== 'undefined' && crypto.getRandomValues) {
    crypto.getRandomValues(bytes);
  } else {
    console.warn('[noteTypes] crypto.getRandomValues unavailable, using Math.random fallback');
    for (let i = 0; i < 8; i++) bytes[i] = Math.floor(Math.random() * 256);
  }
  return Array.from(bytes).map(b => b.toString(16).padStart(2, '0')).join('');
};

/** Create a blank NoteBullet */
export const createNoteBullet = (content = ''): NoteBullet => ({
  Id: generateId(),
  Header: '',
  Content: content,
  IsCollapsed: true,
  ImageDisplayWidth: 200,
  ImageDisplayWidth2: 200,
  CreatedAt: new Date().toISOString(),
  LastEdited: new Date().toISOString(),
  Tags: [],
  Color: '',
  IsPinned: false,
  SortOrder: 0,
  SubBullets: [],
});

/** Create a blank FreeformSection */
export const createFreeformSection = (content = ''): FreeformSection => ({
  Id: generateId(),
  Title: '',
  Content: content,
  CreatedAt: new Date().toISOString(),
});

/** Create a blank NoteDay for today */
export const createNoteDay = (date?: Date): NoteDay => ({
  Date: (date || new Date()).toISOString().split('T')[0] + 'T00:00:00',
  IsFreeformMode: false,
  Bullets: [],
  FreeformSections: [createFreeformSection()],
  LastModified: Date.now(),
});

/** Create a blank TodoItem. SubTasks support nesting but enforce max depth of 3 in the UI. */
export const createTodoItem = (text = ''): TodoItem => ({
  Id: generateId(),
  Text: text,
  IsDone: false,
  CreatedAt: new Date().toISOString(),
  LastEdited: new Date().toISOString(),
  Priority: 0,
  DueDate: null,
  Tags: [],
  Color: '',
  Description: '',
  SubTasks: [],
  Recurrence: 0,
  SortOrder: 0,
  TimerMinutes: null,
  ReminderAt: null,
});

/** Create a blank TodoDay for today */
export const createTodoDay = (date?: Date): TodoDay => ({
  Date: (date || new Date()).toISOString().split('T')[0] + 'T00:00:00',
  Items: [],
  LastModified: Date.now(),
});

/** Parse a date string to Date object (handles C# DateTime format) */
export const parseDate = (dateStr: string): Date => {
  if (!dateStr) return new Date();
  // Handle "2026-06-18T00:00:00" format
  const d = new Date(dateStr);
  if (isNaN(d.getTime())) return new Date(); // Invalid date string — fall back to now
  return d;
};

/** Format date for display: "18, Jun" */
export const formatDisplayDate = (dateStr: string): string => {
  const d = parseDate(dateStr);
  const day = d.getDate().toString().padStart(2, '0');
  const month = d.toLocaleDateString('en-US', { month: 'short' });
  return `${day}, ${month}`;
};

/** Check if a date string represents today */
export const isToday = (dateStr: string): boolean => {
  const d = parseDate(dateStr);
  const today = new Date();
  return d.getFullYear() === today.getFullYear() &&
    d.getMonth() === today.getMonth() &&
    d.getDate() === today.getDate();
};

/** Get due date display text */
export const getDueDateDisplay = (dueDateStr: string | null | undefined): string => {
  if (!dueDateStr) return '';
  const d = new Date(dueDateStr);
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const dDate = new Date(d);
  dDate.setHours(0, 0, 0, 0);
  const diff = dDate.getTime() - today.getTime();
  const daysDiff = Math.floor(diff / (1000 * 60 * 60 * 24));
  if (daysDiff === 0) return 'Today';
  if (daysDiff === 1) return 'Tomorrow';
  if (daysDiff === -1) return 'Yesterday';
  return d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
};

/** Check if a todo item is overdue */
export const isOverdue = (item: TodoItem): boolean => {
  if (!item.DueDate || item.IsDone) return false;
  const d = new Date(item.DueDate);
  d.setHours(0, 0, 0, 0);
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  return d < today;
};
