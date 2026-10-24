/** The Maintenance page's storage report. */
export const STORAGE_KEY = ["maintenance", "storage"] as const;

/** One area's cleanup history. */
export const cleanupsKey = (areaId: string) => ["maintenance", "cleanups", areaId] as const;
