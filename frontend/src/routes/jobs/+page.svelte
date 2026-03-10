<script lang="ts">
	import { onMount, tick } from 'svelte';
	import {
		Loader2,
		Clock,
		CheckCircle2,
		XCircle,
		Zap,
		RefreshCw,
		PackageSearch,
		ChevronDown,
		ChevronRight,
		Terminal,
		CircleCheck,
		CircleX
	} from 'lucide-svelte';
	import type { ProductJobInfo, ProductJobLogEntry } from '$api/types';
	import { products as productsApi } from '$api/client';
	import PageHeader from '$components/layout/PageHeader.svelte';
	import Card from '$components/ui/Card.svelte';
	import Button from '$components/ui/Button.svelte';
	import Modal from '$components/ui/Modal.svelte';

	let activeJobs = $state<ProductJobInfo[]>([]);
	let loading = $state(true);
	let now = $state(Date.now());

	// Log console state
	let expandedJobId = $state<string | null>(null);
	let jobLogs = $state<ProductJobLogEntry[]>([]);
	let logsLoading = $state(false);
	let logPollInterval: ReturnType<typeof setInterval> | null = null;

	// Payload modal state
	let selectedLogEntry = $state<ProductJobLogEntry | null>(null);
	let payloadModalOpen = $state(false);

	function openPayloadModal(entry: ProductJobLogEntry, e: MouseEvent) {
		e.stopPropagation();
		selectedLogEntry = entry;
		payloadModalOpen = true;
	}

	function formatJson(raw: string | null): string {
		if (!raw) return '—';
		try {
			return JSON.stringify(JSON.parse(raw), null, 2);
		} catch {
			return raw;
		}
	}

	let runningCount = $derived(activeJobs.filter((j) => j.status === 'running').length);
	let queuedCount = $derived(activeJobs.filter((j) => j.status === 'queued').length);
	let completedCount = $derived(activeJobs.filter((j) => j.status === 'completed').length);
	let failedCount = $derived(activeJobs.filter((j) => j.status === 'failed').length);

	async function load() {
		try {
			activeJobs = await productsApi.activeJobs();
			if (expandedJobId && !activeJobs.find((j) => j.id === expandedJobId)) {
				closeLogConsole();
			}
		} catch {
			// ignore
		} finally {
			loading = false;
		}
	}

	async function loadLogs(jobId: string) {
		try {
			jobLogs = await productsApi.jobLogs(jobId);
		} catch {
			// ignore
		}
	}

	function startLogPolling(jobId: string) {
		stopLogPolling();
		loadLogs(jobId);
		logPollInterval = setInterval(() => {
			const job = activeJobs.find((j) => j.id === jobId);
			if (job && (job.status === 'running' || job.status === 'queued')) {
				loadLogs(jobId);
			} else {
				loadLogs(jobId);
				stopLogPolling();
			}
		}, 1500);
	}

	function stopLogPolling() {
		if (logPollInterval !== null) {
			clearInterval(logPollInterval);
			logPollInterval = null;
		}
	}

	async function toggleJob(job: ProductJobInfo) {
		if (expandedJobId === job.id) {
			closeLogConsole();
			return;
		}
		expandedJobId = job.id;
		jobLogs = [];
		logsLoading = true;
		await tick();
		logsLoading = false;
		startLogPolling(job.id);
	}

	function closeLogConsole() {
		expandedJobId = null;
		jobLogs = [];
		stopLogPolling();
	}

	function elapsedSeconds(from: string | null, to: string | null): string {
		if (!from) return '—';
		const start = new Date(from).getTime();
		const end = to ? new Date(to).getTime() : now;
		const secs = Math.max(0, Math.floor((end - start) / 1000));
		if (secs < 60) return `${secs}s`;
		const m = Math.floor(secs / 60);
		const s = secs % 60;
		return `${m}m ${s}s`;
	}

	function operationLabel(op: string): string {
		if (op === 'create') return 'Anlegen';
		if (op === 'save') return 'Speichern';
		if (op === 'full') return 'Full Sync';
		return op;
	}

	function formatTime(ts: string): string {
		return new Date(ts).toLocaleTimeString('de-DE', {
			hour: '2-digit',
			minute: '2-digit',
			second: '2-digit'
		});
	}

	function shortEndpoint(endpoint: string): string {
		try {
			const url = new URL(endpoint);
			const parts = url.pathname.split('/').filter(Boolean);
			return parts[parts.length - 1] ?? endpoint;
		} catch {
			return endpoint.split('.').pop() ?? endpoint;
		}
	}

	onMount(() => {
		load();
		const pollInterval = setInterval(() => load(), 3000);
		const clockInterval = setInterval(() => (now = Date.now()), 1000);
		return () => {
			clearInterval(pollInterval);
			clearInterval(clockInterval);
			stopLogPolling();
		};
	});
</script>

<svelte:head>
	<title>Jobs | Actindo Middleware</title>
</svelte:head>

<PageHeader title="Jobs" subtitle="Aktive und wartende Produkt-Sync-Jobs">
	{#snippet actions()}
		<div class="flex items-center gap-3">
			{#if runningCount > 0 || queuedCount > 0}
				<div class="flex items-center gap-2 px-3 py-1.5 rounded-full bg-royal-600/20 border border-royal-500/30">
					<Loader2 size={14} class="animate-spin text-royal-400" />
					<span class="text-xs text-royal-300 font-medium">Live · alle 3s</span>
				</div>
			{/if}
			<Button variant="ghost" onclick={load} disabled={loading}>
				<RefreshCw size={16} class={loading ? 'animate-spin' : ''} />
				Aktualisieren
			</Button>
		</div>
	{/snippet}
</PageHeader>

<!-- Summary Cards -->
<div class="grid grid-cols-2 sm:grid-cols-4 gap-4 mb-6">
	<div class="rounded-xl border bg-royal-600/10 border-royal-500/30 p-4">
		<div class="flex items-center gap-2 mb-1">
			<Loader2 size={16} class="text-royal-400 {runningCount > 0 ? 'animate-spin' : ''}" />
			<span class="text-xs text-gray-400 uppercase tracking-wide">Läuft</span>
		</div>
		<p class="text-2xl font-bold text-royal-300">{runningCount}</p>
	</div>
	<div class="rounded-xl border bg-white/5 border-white/10 p-4">
		<div class="flex items-center gap-2 mb-1">
			<Clock size={16} class="text-gray-400" />
			<span class="text-xs text-gray-400 uppercase tracking-wide">Wartend</span>
		</div>
		<p class="text-2xl font-bold text-gray-300">{queuedCount}</p>
	</div>
	<div class="rounded-xl border bg-green-900/10 border-green-500/20 p-4">
		<div class="flex items-center gap-2 mb-1">
			<CheckCircle2 size={16} class="text-green-400" />
			<span class="text-xs text-gray-400 uppercase tracking-wide">Fertig</span>
		</div>
		<p class="text-2xl font-bold text-green-300">{completedCount}</p>
	</div>
	<div class="rounded-xl border bg-red-900/10 border-red-500/20 p-4">
		<div class="flex items-center gap-2 mb-1">
			<XCircle size={16} class="text-red-400" />
			<span class="text-xs text-gray-400 uppercase tracking-wide">Fehler</span>
		</div>
		<p class="text-2xl font-bold text-red-300">{failedCount}</p>
	</div>
</div>

<!-- Jobs Table -->
<Card>
	{#if loading && activeJobs.length === 0}
		<div class="flex justify-center py-16">
			<Loader2 size={32} class="animate-spin text-royal-400" />
		</div>
	{:else if activeJobs.length === 0}
		<div class="text-center py-16 text-gray-400">
			<PackageSearch size={48} class="mx-auto mb-4 opacity-40" />
			<p class="font-medium mb-1">Keine aktiven Jobs</p>
			<p class="text-sm text-gray-500">Jobs erscheinen hier sobald ein Produkt-Sync mit await=false gestartet wird</p>
		</div>
	{:else}
		<div class="overflow-x-auto">
			<table class="w-full text-sm">
				<thead>
					<tr class="border-b border-white/10 text-left">
						<th class="pb-3 pr-2 w-6"></th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">Status</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">SKU</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">Operation</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">Queue-Zeit</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">Laufzeit</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">Buffer ID</th>
						<th class="pb-3 font-medium text-gray-400">Fehler</th>
					</tr>
				</thead>
				<tbody class="divide-y divide-white/5">
					{#each activeJobs as job (job.id)}
						<!-- Job row -->
						<tr
							class="cursor-pointer transition-colors hover:bg-white/5
								{job.status === 'running'
								? 'bg-royal-600/5'
								: job.status === 'failed'
									? 'bg-red-900/5'
									: ''}
								{expandedJobId === job.id ? 'bg-white/5' : ''}"
							onclick={() => toggleJob(job)}
							title="Klicken für API-Log"
						>
							<!-- Expand indicator -->
							<td class="py-3 pr-2 text-gray-500">
								{#if expandedJobId === job.id}
									<ChevronDown size={14} />
								{:else}
									<ChevronRight size={14} />
								{/if}
							</td>

							<!-- Status -->
							<td class="py-3 pr-4">
								<div class="flex items-center gap-2 whitespace-nowrap">
									{#if job.status === 'running'}
										<Loader2 size={15} class="animate-spin text-royal-400 shrink-0" />
										<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-royal-600/30 text-royal-300">
											Läuft
										</span>
									{:else if job.status === 'queued'}
										<Clock size={15} class="text-gray-400 shrink-0" />
										<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-gray-700 text-gray-300">
											Wartet
										</span>
									{:else if job.status === 'completed'}
										<CheckCircle2 size={15} class="text-green-400 shrink-0" />
										<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-green-900/40 text-green-300">
											Fertig
										</span>
									{:else}
										<XCircle size={15} class="text-red-400 shrink-0" />
										<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-red-900/40 text-red-300">
											Fehler
										</span>
									{/if}
								</div>
							</td>

							<!-- SKU -->
							<td class="py-3 pr-4">
								<span class="font-mono font-medium text-white">{job.sku}</span>
							</td>

							<!-- Operation -->
							<td class="py-3 pr-4">
								<div class="flex items-center gap-1.5">
									<Zap size={13} class="text-royal-400 shrink-0" />
									<span class="text-gray-300">{operationLabel(job.operation)}</span>
								</div>
							</td>

							<!-- Queue-Zeit -->
							<td class="py-3 pr-4 text-gray-400 tabular-nums whitespace-nowrap">
								{#if job.startedAt}
									{elapsedSeconds(job.queuedAt, job.startedAt)}
								{:else}
									<span class="text-royal-400">{elapsedSeconds(job.queuedAt, null)}</span>
								{/if}
							</td>

							<!-- Laufzeit -->
							<td class="py-3 pr-4 tabular-nums whitespace-nowrap">
								{#if job.status === 'running'}
									<span class="text-royal-300">{elapsedSeconds(job.startedAt, null)}</span>
								{:else if job.startedAt}
									<span class="text-gray-400">{elapsedSeconds(job.startedAt, job.completedAt)}</span>
								{:else}
									<span class="text-gray-600">—</span>
								{/if}
							</td>

							<!-- Buffer ID -->
							<td class="py-3 pr-4">
								{#if job.bufferId}
									<span class="font-mono text-xs text-gray-400 max-w-[120px] truncate block" title={job.bufferId}>
										{job.bufferId}
									</span>
								{:else}
									<span class="text-gray-600">—</span>
								{/if}
							</td>

							<!-- Fehler -->
							<td class="py-3">
								{#if job.error}
									<span class="text-xs text-red-400 max-w-[200px] truncate block" title={job.error}>
										{job.error}
									</span>
								{:else}
									<span class="text-gray-600">—</span>
								{/if}
							</td>
						</tr>

						<!-- Log console row -->
						{#if expandedJobId === job.id}
							<tr>
								<td colspan="8" class="pb-3 pt-0">
									<div class="mx-1 rounded-lg border border-white/10 bg-gray-950/80 overflow-hidden">
										<!-- Console header -->
										<div class="flex items-center gap-2 px-3 py-2 border-b border-white/10 bg-black/30">
											<Terminal size={13} class="text-royal-400" />
											<span class="text-xs font-mono font-medium text-royal-300">
												Actindo API Log — {job.sku}
											</span>
											{#if job.status === 'running'}
												<div class="flex items-center gap-1.5 ml-auto">
													<span class="inline-block w-1.5 h-1.5 rounded-full bg-royal-400 animate-pulse"></span>
													<span class="text-xs text-royal-400 font-mono">live</span>
												</div>
											{:else}
												<span class="text-xs text-gray-500 font-mono ml-auto">
													{jobLogs.length} {jobLogs.length === 1 ? 'Eintrag' : 'Einträge'} · Eintrag anklicken für Payload
												</span>
											{/if}
										</div>

										<!-- Console body -->
										<div class="font-mono text-xs p-3 space-y-1.5 max-h-64 overflow-y-auto">
											{#if jobLogs.length === 0}
												{#if job.status === 'queued'}
													<p class="text-gray-500 italic">Wartet auf freien Slot...</p>
												{:else if job.status === 'running'}
													<div class="flex items-center gap-2 text-gray-500">
														<Loader2 size={12} class="animate-spin text-royal-500" />
														<span class="italic">Warte auf ersten API-Call...</span>
													</div>
												{:else}
													<p class="text-gray-500 italic">Keine API-Calls aufgezeichnet.</p>
												{/if}
											{:else}
												{#each jobLogs as entry}
													<!-- svelte-ignore a11y_click_events_have_key_events a11y_interactive_supports_focus -->
													<div
														class="flex items-start gap-2 group cursor-pointer rounded px-1 -mx-1 hover:bg-white/5 transition-colors"
														onclick={(e) => openPayloadModal(entry, e)}
														title="Klicken für Request/Response Payload"
														role="button"
														tabindex="0"
													>
														<!-- Status icon -->
														<div class="mt-0.5 shrink-0">
															{#if entry.success}
																<CircleCheck size={12} class="text-green-400" />
															{:else}
																<CircleX size={12} class="text-red-400" />
															{/if}
														</div>

														<!-- Timestamp -->
														<span class="text-gray-600 shrink-0 tabular-nums">
															{formatTime(entry.timestamp)}
														</span>

														<!-- Endpoint + error -->
														<div class="min-w-0 flex-1">
															<span class="{entry.success ? 'text-green-300' : 'text-red-300'} break-all">
																{shortEndpoint(entry.endpoint)}
															</span>
															{#if entry.error}
																<span class="text-red-400/80 ml-2 break-all">→ {entry.error}</span>
															{/if}
														</div>

														<!-- Click hint -->
														<span class="text-gray-600 group-hover:text-gray-400 shrink-0 text-[10px] opacity-0 group-hover:opacity-100 transition-opacity">
															payload
														</span>
													</div>
												{/each}

												{#if job.status === 'running'}
													<div class="flex items-center gap-2 text-gray-500 pt-0.5">
														<Loader2 size={11} class="animate-spin text-royal-500 shrink-0" />
														<span class="italic">Läuft...</span>
													</div>
												{/if}
											{/if}
										</div>
									</div>
								</td>
							</tr>
						{/if}
					{/each}
				</tbody>
			</table>
		</div>
		<p class="text-xs text-gray-600 mt-4">
			Erfolgreiche Jobs werden nach 5 Tagen automatisch entfernt. Fehlgeschlagene nach 5 Minuten. Klick auf eine Zeile für den API-Log.
		</p>
	{/if}
</Card>

<!-- Payload Modal -->
{#if selectedLogEntry}
	<Modal
		bind:open={payloadModalOpen}
		title={shortEndpoint(selectedLogEntry.endpoint)}
		class="max-w-4xl"
		onclose={() => (selectedLogEntry = null)}
	>
		<div class="space-y-4">
			<!-- Full endpoint -->
			<p class="text-xs text-gray-500 font-mono break-all -mt-2">{selectedLogEntry.endpoint}</p>

			<div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
				<!-- Request -->
				<div>
					<div class="flex items-center gap-2 mb-2">
						<span class="text-xs font-semibold text-gray-400 uppercase tracking-wide">Request</span>
					</div>
					<pre class="text-xs font-mono bg-black/40 border border-white/10 rounded-lg p-3 overflow-auto max-h-96 text-gray-300 whitespace-pre-wrap break-all">{formatJson(selectedLogEntry.requestPayload)}</pre>
				</div>

				<!-- Response -->
				<div>
					<div class="flex items-center gap-2 mb-2">
						<span class="text-xs font-semibold text-gray-400 uppercase tracking-wide">Response</span>
						{#if !selectedLogEntry.success}
							<span class="text-xs px-1.5 py-0.5 rounded bg-red-900/40 text-red-400">Fehler</span>
						{/if}
					</div>
					<pre class="text-xs font-mono bg-black/40 border border-white/10 rounded-lg p-3 overflow-auto max-h-96 {selectedLogEntry.success ? 'text-gray-300' : 'text-red-300'} whitespace-pre-wrap break-all">{formatJson(selectedLogEntry.responsePayload)}</pre>
				</div>
			</div>
		</div>
	</Modal>
{/if}
