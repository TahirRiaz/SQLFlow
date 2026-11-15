import { createContext } from "react";

/**
 * How the DetailPairs under it lay out a caption and its value: over each other (a panel's column, a sheet, the default)
 * or side by side, flowing along a line (DetailHeaderCard's facts, where a dozen of them take a line or two).
 */
export type DetailPairLayout = "stacked" | "inline";

export const DetailPairLayoutContext = createContext<DetailPairLayout>("stacked");
