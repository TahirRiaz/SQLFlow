/** A tab of a pipeline's page, a kind's own or a built-in one, by the value the address names it with (`?tab=`). */
export interface PipelineTabEntry {
  value: string;
}

/**
 * A pipeline page's tabs in the order its kind asks (`FlowKindContribution.pipelineTabOrder`): the tabs it names, in its
 * order, then the others as they come (the kind's own, then the built-in ones). A name that is no tab of the page, such
 * as a tab the kind hides or one this version does not have, is passed over, and a name given twice keeps its first
 * place, so an order written against one version of the page keeps working on the next.
 */
export function orderPipelineTabs<T extends PipelineTabEntry>(tabs: readonly T[], order: readonly string[] | undefined): T[] {
  if (order === undefined || order.length === 0) {
    return [...tabs];
  }

  const rank = new Map<string, number>();
  for (const value of order) {
    if (!rank.has(value)) {
      rank.set(value, rank.size);
    }
  }

  const named = tabs.filter((tab) => rank.has(tab.value));
  named.sort((a, b) => (rank.get(a.value) ?? 0) - (rank.get(b.value) ?? 0));
  return [...named, ...tabs.filter((tab) => !rank.has(tab.value))];
}
