import { useQuery } from "@tanstack/react-query";
import { scheduleApi } from "../../api/endpoints";
import type { Schedule } from "../../api/types";
import type { FilterOption } from "../../components/FilterCombobox";
import { fetchEveryPage } from "../../lib/fetchEveryPage";

/** The most schedules a filter pulls into the browser. An estate runs hundreds of them, one or a few per source, so
 * this is far past any real estate and only bounds a runaway catalog. */
const SCHEDULE_CAP = 5000;

/**
 * Every schedule in the estate, for a filter that offers them all and for mapping a flow to the schedule that runs it.
 * Read page by page, because the API answers at most 200 a page and a filter cut there would leave the schedules
 * after it unpickable with nothing to say so. Keyed under "schedules", so a create, pause or delete refreshes it.
 */
export function useAllSchedules() {
  return useQuery({
    queryKey: ["schedules", "all"],
    queryFn: () => fetchEveryPage((page, pageSize) => scheduleApi.list({ page, pageSize }), SCHEDULE_CAP),
    staleTime: 60_000,
  });
}

/** A schedule filter's options: one per schedule, valued by id (so two like-named schedules in different repos stay
 * distinct), hinted with how many flows a fire runs, in the server's name order. */
export function scheduleFilterOptions(schedules: readonly Schedule[]): FilterOption[] {
  return schedules.map((schedule) => ({
    value: schedule.id,
    label: schedule.name,
    hint: `${schedule.memberPipelineIds.length} ${schedule.memberPipelineIds.length === 1 ? "flow" : "flows"}`,
  }));
}
