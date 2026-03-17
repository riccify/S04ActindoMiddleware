import { writable } from 'svelte/store';
import type { DashboardSummary } from '$api/types';
import { dashboard as dashboardApi } from '$api/client';

interface DashboardState {
	summary: DashboardSummary | null;
	loading: boolean;
	error: string | null;
}

const POLL_INTERVAL_MS = 60000; // 1 Minute

function createDashboardStore() {
	const { subscribe, set, update } = writable<DashboardState>({
		summary: null,
		loading: false,
		error: null
	});

	let pollInterval: ReturnType<typeof setInterval> | null = null;

	return {
		subscribe,

		async load() {
			update((s) => ({ ...s, loading: true, error: null }));
			try {
				const summary = await dashboardApi.summary();
				set({ summary, loading: false, error: null });
			} catch (e) {
				update((s) => ({
					...s,
					loading: false,
					error: e instanceof Error ? e.message : 'Fehler beim Laden'
				}));
			}
		},

		startPolling() {
			if (pollInterval) return;

			// Initial load
			this.load();

			// Start polling
			pollInterval = setInterval(() => {
				this.load();
			}, POLL_INTERVAL_MS);
		},

		stopPolling() {
			if (pollInterval) {
				clearInterval(pollInterval);
				pollInterval = null;
			}
		},

		clear() {
			this.stopPolling();
			set({ summary: null, loading: false, error: null });
		}
	};
}

export const dashboardStore = createDashboardStore();
