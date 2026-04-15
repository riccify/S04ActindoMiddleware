<script lang="ts">
	import { onMount, tick } from 'svelte';
	import { slide } from 'svelte/transition';
	import { cubicOut } from 'svelte/easing';
	import {
		Loader2,
		Clock,
		CheckCircle2,
		XCircle,
		Zap,
		RefreshCw,
		PackageSearch,
		ChevronRight,
		Terminal,
		CircleCheck,
		CircleX,
		Play,
		Trash2,
		RotateCcw,
		AlertTriangle,
		Search,
		FileText
	} from 'lucide-svelte';
	import type { ProductJobInfo, ProductJobListItem, ProductJobLogEntry } from '$api/types';
	import { products as productsApi, settings as settingsApi } from '$api/client';
	import PageHeader from '$components/layout/PageHeader.svelte';
	import Card from '$components/ui/Card.svelte';
	import Button from '$components/ui/Button.svelte';
	import Modal from '$components/ui/Modal.svelte';
	
	type JobsTab = 'products' | 'customers' | 'transactions';
	const PAGE_SIZE = 20;

	let activeJobs = $state<ProductJobListItem[]>([]);
	let loading = $state(true);
	let now = $state(Date.now());
	let activeTab = $state<JobsTab>('products');
	let currentPage = $state(1);

	// Filter state
	let skuSearch = $state('');
	let showErrorsOnly = $state(false);
	let retryingJobId = $state<string | null>(null);
	let deletingSuccessfulJobs = $state(false);

	// NAV payload modal
	let navPayloadJob = $state<ProductJobInfo | null>(null);
	let navPayloadModalOpen = $state(false);
	let navPayloadLoading = $state(false);

	// Log console state
	let expandedJobId = $state<string | null>(null);
	let jobLogs = $state<ProductJobLogEntry[]>([]);
	let logPollInterval: ReturnType<typeof setInterval> | null = null;
	let jobsWithErrors = $state<Set<string>>(new Set());

	// Payload modal state
	let selectedLogEntry = $state<ProductJobLogEntry | null>(null);
	let payloadModalOpen = $state(false);
	let editablePayload = $state('');
	let replayLoading = $state(false);
	let replayResponsePayload = $state<string | null>(null);
	let replaySuccess = $state<boolean | null>(null);
	let replayError = $state<string | null>(null);
	let newJobModalOpen = $state(false);
	let newJobEndpoint = $state('');
	let newJobPayload = $state('{}');
	let availableEndpoints = $state<Array<{ key: string; value: string }>>([]);

	function openPayloadModal(entry: ProductJobLogEntry, e: MouseEvent) {
		e.stopPropagation();
		selectedLogEntry = entry;
		editablePayload = formatJson(entry.requestPayload);
		replayResponsePayload = null;
		replaySuccess = null;
		replayError = null;
		payloadModalOpen = true;
	}

	async function handleReplay() {
		if (!selectedLogEntry) return;
		await sendCustomRequest(selectedLogEntry.endpoint, editablePayload);
	}

	async function handleNewJobSubmit() {
		await sendCustomRequest(newJobEndpoint, newJobPayload);
	}

	async function sendCustomRequest(endpoint: string, payload: string) {
		replayLoading = true;
		replayResponsePayload = null;
		replaySuccess = null;
		replayError = null;
		try {
			const result = await productsApi.logReplay(endpoint, payload);
			replaySuccess = result.success;
			replayResponsePayload = result.responsePayload ? formatJson(result.responsePayload) : null;
			replayError = result.error ?? null;
		} catch (err) {
			replaySuccess = false;
			replayError = err instanceof Error ? err.message : 'Unbekannter Fehler';
		} finally {
			replayLoading = false;
		}
	}

	async function openNavPayloadModal(job: ProductJobListItem, e: MouseEvent) {
		e.stopPropagation();
		navPayloadLoading = true;
		navPayloadJob = null;
		navPayloadModalOpen = true;
		try {
			navPayloadJob = await productsApi.job(job.id);
		} catch {
			navPayloadJob = null;
		} finally {
			navPayloadLoading = false;
		}
	}

	function openNewJobModal() {
		newJobEndpoint = availableEndpoints[0]?.value ?? '';
		newJobPayload = '{}';
		replayResponsePayload = null;
		replaySuccess = null;
		replayError = null;
		newJobModalOpen = true;
	}

	async function loadAvailableEndpoints() {
		try {
			const settings = await settingsApi.get();
			availableEndpoints = Object.entries(settings.endpoints ?? {})
				.filter(([, value]) => value.trim().length > 0)
				.sort(([left], [right]) => left.localeCompare(right))
				.map(([key, value]) => ({ key, value }));

			if (!newJobEndpoint && availableEndpoints.length > 0) {
				newJobEndpoint = availableEndpoints[0].value;
			}
		} catch {
			availableEndpoints = [];
		}
	}

	async function retryJob(job: ProductJobListItem, e: MouseEvent) {
		e.stopPropagation();
		if (retryingJobId === job.id) return;
		retryingJobId = job.id;
		try {
			await productsApi.retryJob(job.id);
			await load();
		} catch {
			// ignore
		} finally {
			retryingJobId = null;
		}
	}

	async function deleteJob(job: ProductJobListItem, e: MouseEvent) {
		e.stopPropagation();
		try {
			await productsApi.deleteJob(job.id);
			activeJobs = activeJobs.filter((j) => j.id !== job.id);
			if (expandedJobId === job.id) closeLogConsole();
		} catch {
			// ignore
		}
	}

	function formatJson(raw: string | null): string {
		if (!raw) return '—';
		try {
			return JSON.stringify(JSON.parse(raw), null, 2);
		} catch {
			return raw;
		}
	}

	// --- Log entry metadata parsing ---

	type LogEntryMeta =
		| { type: 'master'; sku: string; actindoId: string | null }
		| { type: 'variant'; sku: string; actindoId: string | null }
		| { type: 'relation'; variantId: string; parentId: string }
		| { type: 'inventory'; sku: string; warehouseId: string | null }
		| { type: 'image-file'; productRef: string; path: string | null }
		| { type: 'image-link'; productRef: string; imageIds: string[] }
		| { type: 'nav'; requestType: string; productRef: string | null; navId: string | null; actindoId: string | null }
		| { type: 'unknown' };

	function displayJobSku(rawSku: string): string {
		if (rawSku.startsWith('product-image:')) {
			return rawSku.slice('product-image:'.length);
		}

		if (rawSku.startsWith('transactions:')) {
			return rawSku.slice('transactions:'.length);
		}

		return rawSku;
	}

	function isTransactionJob(job: ProductJobListItem): boolean {
		return job.operation.includes('transaction');
	}

	function isCustomerJob(job: ProductJobListItem): boolean {
		return job.operation === 'customer-create' || job.operation === 'customer-save';
	}

	function isProductJob(job: ProductJobListItem): boolean {
		return !isTransactionJob(job) && !isCustomerJob(job);
	}

	function jobMatchesTab(job: ProductJobListItem, tab: JobsTab): boolean {
		if (tab === 'products') return isProductJob(job);
		if (tab === 'customers') return isCustomerJob(job);
		return isTransactionJob(job);
	}

	function setActiveTab(tab: JobsTab) {
		activeTab = tab;
		currentPage = 1;
	}

	function extractProductRefFromJob(job: ProductJobListItem): string | null {
		if (job.operation === 'image-upload' && job.sku.startsWith('product-image:')) {
			return displayJobSku(job.sku);
		}
		return displayJobSku(job.sku) || null;
	}

	function parseLogEntryMeta(entry: ProductJobLogEntry, job: ProductJobListItem): LogEntryMeta {
		try {
			const req = entry.requestPayload ? JSON.parse(entry.requestPayload) : null;
			const res = entry.responsePayload ? JSON.parse(entry.responsePayload) : null;
			const jobSku = displayJobSku(job.sku);
			const productRef = extractProductRefFromJob(job);

			if (req?.requestType !== undefined) {
				const requestType = String(req.requestType);
				const navId =
					req?.customer?.nav_id != null ? String(req.customer.nav_id) :
					req?.products?.[0]?.nav_id != null ? String(req.products[0].nav_id) :
					req?.nav_id != null ? String(req.nav_id) :
					null;
				const actindoId =
					req?.customer?.actindo_id != null ? String(req.customer.actindo_id) :
					req?.products?.[0]?.actindo_id != null ? String(req.products[0].actindo_id) :
					req?.actindo_id != null ? String(req.actindo_id) :
					null;
				const navProductRef =
					req?.sku != null ? String(req.sku) :
					req?.products?.[0]?.nav_id != null ? String(req.products[0].nav_id) :
					productRef;
				return { type: 'nav', requestType, productRef: navProductRef, navId, actindoId };
			}

			if (req?.product?.sku !== undefined) {
				const sku: string = req.product.sku;
				const isMaster = sku === jobSku || sku === `${jobSku}-INDI`;
				const actindoId: string | null =
					res?.product?.id != null ? String(res.product.id) :
					res?.product?.entityId != null ? String(res.product.entityId) :
					res?.productId != null ? String(res.productId) :
					null;
				return { type: isMaster ? 'master' : 'variant', sku, actindoId };
			}

			if (req?.variantProduct?.id !== undefined) {
				return {
					type: 'relation',
					variantId: String(req.variantProduct.id),
					parentId: String(req.parentProduct?.id ?? '?')
				};
			}

			if (req?.inventory?.sku !== undefined) {
				const warehouseId = req.inventory['_fulfillment_inventory_warehouse'];
				return {
					type: 'inventory',
					sku: req.inventory.sku,
					warehouseId: warehouseId != null ? String(warehouseId) : null
				};
			}

			if (req?.path !== undefined || entry.endpoint.includes('CreateFile')) {
				return {
					type: 'image-file',
					productRef: productRef ?? 'Produktbild',
					path: req?.path != null ? String(req.path) : null
				};
			}

			if (req?.product?.id !== undefined && req?.product?._pim_images?.images !== undefined) {
				return {
					type: 'image-link',
					productRef: `Produkt #${String(req.product.id)}`,
					imageIds: Array.isArray(req.product._pim_images.images)
						? req.product._pim_images.images
								.map((image: { id?: unknown }) => image?.id != null ? String(image.id) : null)
								.filter((id: string | null): id is string => !!id)
						: []
				};
			}
		} catch {
			// ignore
		}
		return { type: 'unknown' };
	}

	let tabJobs = $derived(activeJobs.filter((job) => jobMatchesTab(job, activeTab)));

	let runningCount = $derived(tabJobs.filter((j) => j.status === 'running').length);
	let queuedCount = $derived(tabJobs.filter((j) => j.status === 'queued').length);
	let completedCount = $derived(tabJobs.filter((j) => j.status === 'completed').length);
	let failedCount = $derived(tabJobs.filter((j) => j.status === 'failed').length);

	let filteredJobs = $derived(
		tabJobs.filter((j) => {
			if (showErrorsOnly && j.status !== 'failed' && !jobsWithErrors.has(j.id)) return false;
			if (skuSearch.trim() && !j.sku.toLowerCase().includes(skuSearch.trim().toLowerCase()))
				return false;
			return true;
		}).toSorted((a, b) => new Date(b.queuedAt).getTime() - new Date(a.queuedAt).getTime())
	);

	let totalPages = $derived(Math.max(1, Math.ceil(filteredJobs.length / PAGE_SIZE)));
	let currentPageSafe = $derived(Math.min(currentPage, totalPages));
	let pagedJobs = $derived(
		filteredJobs.slice((currentPageSafe - 1) * PAGE_SIZE, currentPageSafe * PAGE_SIZE)
	);

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
			if (jobLogs.some((e) => !e.success)) {
				jobsWithErrors = new Set([...jobsWithErrors, jobId]);
			}
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

	async function toggleJob(job: ProductJobListItem) {
		if (expandedJobId === job.id) {
			closeLogConsole();
			return;
		}
		expandedJobId = job.id;
		jobLogs = [];
		await tick();
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
		if (op === 'inventory') return 'Bestand';
		if (op === 'price') return 'Preis';
		if (op === 'image-upload') return 'Bilder';
		if (op === 'customer-create') return 'Debitor anlegen';
		if (op === 'customer-save') return 'Debitor speichern';
		if (op === 'transaction-get') return 'Transaktionen laden';
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

	async function deleteAllSuccessfulJobs() {
		if (deletingSuccessfulJobs) return;
		deletingSuccessfulJobs = true;
		try {
			await productsApi.deleteSuccessfulJobs();
			if (expandedJobId && !activeJobs.find((job) => job.id === expandedJobId && job.status !== 'completed')) {
				closeLogConsole();
			}
			await load();
		} catch {
			// ignore
		} finally {
			deletingSuccessfulJobs = false;
		}
	}

	function goToPreviousPage() {
		currentPage = Math.max(1, currentPageSafe - 1);
	}

	function goToNextPage() {
		currentPage = Math.min(totalPages, currentPageSafe + 1);
	}

	onMount(() => {
		load();
		loadAvailableEndpoints();
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

<PageHeader title="Jobs" subtitle="Aktive und wartende Sync-Jobs">
	{#snippet actions()}
		<div class="flex items-center gap-3">
			<Button variant="secondary" onclick={openNewJobModal}>
				<Play size={16} />
				Neuer Job
			</Button>
			<Button variant="ghost" onclick={deleteAllSuccessfulJobs} disabled={deletingSuccessfulJobs}>
				{#if deletingSuccessfulJobs}
					<Loader2 size={16} class="animate-spin" />
				{:else}
					<Trash2 size={16} />
				{/if}
				Alle erfolgreichen Jobs löschen
			</Button>
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
	<div class="flex flex-wrap items-center gap-2 mb-5">
		<button
			type="button"
			onclick={() => setActiveTab('products')}
			class="px-3 py-1.5 rounded-lg text-sm font-medium transition-colors border
				{activeTab === 'products'
					? 'bg-royal-600/20 border-royal-500/40 text-royal-300'
					: 'bg-white/5 border-white/10 text-gray-400 hover:text-gray-200'}"
		>
			Produkt Jobs
		</button>
		<button
			type="button"
			onclick={() => setActiveTab('customers')}
			class="px-3 py-1.5 rounded-lg text-sm font-medium transition-colors border
				{activeTab === 'customers'
					? 'bg-royal-600/20 border-royal-500/40 text-royal-300'
					: 'bg-white/5 border-white/10 text-gray-400 hover:text-gray-200'}"
		>
			Debitoren Jobs
		</button>
		<button
			type="button"
			onclick={() => setActiveTab('transactions')}
			class="px-3 py-1.5 rounded-lg text-sm font-medium transition-colors border
				{activeTab === 'transactions'
					? 'bg-royal-600/20 border-royal-500/40 text-royal-300'
					: 'bg-white/5 border-white/10 text-gray-400 hover:text-gray-200'}"
		>
			Transaktionen
		</button>
	</div>

	<!-- Filter controls -->
	<div class="flex flex-wrap items-center gap-3 mb-5">
		<div class="relative flex-1 min-w-[200px] max-w-xs">
			<Search size={14} class="absolute left-3 top-1/2 -translate-y-1/2 text-gray-500" />
			<input
				type="search"
				placeholder="SKU suchen..."
				bind:value={skuSearch}
				class="w-full bg-white/5 border border-white/10 rounded-lg pl-8 pr-3 py-1.5 text-sm text-gray-300 placeholder-gray-600 focus:outline-none focus:border-royal-500/50"
			/>
		</div>
		<button
			type="button"
			onclick={() => (showErrorsOnly = !showErrorsOnly)}
			class="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium transition-colors
				{showErrorsOnly
				? 'bg-red-900/40 border border-red-500/40 text-red-300'
				: 'bg-white/5 border border-white/10 text-gray-400 hover:text-gray-200'}"
		>
			<AlertTriangle size={13} />
			Nur Fehler
		</button>
		{#if skuSearch || showErrorsOnly}
			<span class="text-xs text-gray-500">{filteredJobs.length} / {tabJobs.length}</span>
		{/if}
	</div>

	{#if filteredJobs.length > 0}
		<div class="flex flex-wrap items-center justify-between gap-3 mb-5">
			<span class="text-xs text-gray-500">
				Seite {currentPageSafe} von {totalPages} · {filteredJobs.length} Einträge
			</span>
			<div class="flex items-center gap-2">
				<button
					type="button"
					onclick={goToPreviousPage}
					disabled={currentPageSafe <= 1}
					class="px-3 py-1.5 rounded-lg text-xs font-medium border transition-colors
						bg-white/5 border-white/10 text-gray-300 hover:text-white disabled:opacity-40 disabled:cursor-not-allowed"
				>
					Vorherige
				</button>
				<button
					type="button"
					onclick={goToNextPage}
					disabled={currentPageSafe >= totalPages}
					class="px-3 py-1.5 rounded-lg text-xs font-medium border transition-colors
						bg-white/5 border-white/10 text-gray-300 hover:text-white disabled:opacity-40 disabled:cursor-not-allowed"
				>
					Nächste
				</button>
			</div>
		</div>
	{/if}

	{#if loading && activeJobs.length === 0}
		<div class="flex justify-center py-16">
			<Loader2 size={32} class="animate-spin text-royal-400" />
		</div>
	{:else if tabJobs.length === 0}
		<div class="text-center py-16 text-gray-400">
			<PackageSearch size={48} class="mx-auto mb-4 opacity-40" />
			<p class="font-medium mb-1">Keine Jobs in diesem Tab</p>
			<p class="text-sm text-gray-500">Sobald hier neue Vorgänge laufen, erscheinen sie in dieser Ansicht</p>
		</div>
	{:else}
		<div class="overflow-x-auto">
			<table class="w-full text-sm">
				<thead>
					<tr class="border-b border-white/10 text-left">
						<th class="pb-3 pr-1 w-6"></th>
						<th class="pb-3 pr-1 w-6"></th>
						<th class="pb-3 pr-1 w-6"></th>
						<th class="pb-3 pr-2 w-6"></th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">Status</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">Zeitstempel</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">SKU</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">Operation</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">Dauer</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 whitespace-nowrap">Buffer ID</th>
						<th class="pb-3 font-medium text-gray-400">Fehler</th>
					</tr>
				</thead>
				<tbody class="divide-y divide-white/5">
					{#each pagedJobs as job (job.id)}
						<!-- Job row -->
						<tr
							class="cursor-pointer transition-colors hover:bg-white/5
								{job.status === 'running' ? 'bg-royal-600/5' : (job.status === 'failed' || jobsWithErrors.has(job.id)) ? 'bg-red-900/5' : ''}
								{expandedJobId === job.id ? 'bg-white/5' : ''}"
							onclick={() => toggleJob(job)}
							title="Klicken für API-Log"
						>
							<!-- Delete button -->
							<td class="py-3 pr-1 w-6">
								<button
									type="button"
									class="text-gray-600 hover:text-red-400 transition-colors p-0.5 rounded"
									onclick={(e) => deleteJob(job, e)}
									title="Job löschen"
								>
									<Trash2 size={13} />
								</button>
							</td>

							<!-- Retry button -->
							<td class="py-3 pr-1 w-6">
								{#if job.operation === 'create' || job.operation === 'save' || job.operation === 'full'}
									<button
										type="button"
										class="text-gray-600 hover:text-green-400 transition-colors p-0.5 rounded disabled:opacity-30"
										onclick={(e) => retryJob(job, e)}
										disabled={retryingJobId === job.id || job.status === 'running' || job.status === 'queued'}
										title="Job wiederholen"
									>
										{#if retryingJobId === job.id}
											<Loader2 size={13} class="animate-spin" />
										{:else}
											<RotateCcw size={13} />
										{/if}
									</button>
								{:else}
									<span class="w-4 inline-block"></span>
								{/if}
							</td>

							<!-- NAV payload button -->
							<td class="py-3 pr-2 w-6">
								<button
									type="button"
									class="text-gray-600 hover:text-royal-400 transition-colors p-0.5 rounded"
									onclick={(e) => openNavPayloadModal(job, e)}
									title="Job-Details anzeigen"
								>
									<FileText size={13} />
								</button>
							</td>

							<!-- Expand indicator -->
							<td class="py-3 pr-2 text-gray-500">
								<div class="transition-transform duration-200 {expandedJobId === job.id ? 'rotate-90' : ''}">
									<ChevronRight size={14} />
								</div>
							</td>

							<!-- Status -->
							<td class="py-3 pr-4">
								<div class="flex items-center gap-2 whitespace-nowrap">
									{#if job.status === 'running'}
										<Loader2 size={15} class="animate-spin text-royal-400 shrink-0" />
										<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-royal-600/30 text-royal-300">Läuft</span>
									{:else if job.status === 'queued'}
										<Clock size={15} class="text-gray-400 shrink-0" />
										<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-gray-700 text-gray-300">Wartet</span>
									{:else if job.status === 'completed' && !jobsWithErrors.has(job.id)}
										<CheckCircle2 size={15} class="text-green-400 shrink-0" />
										<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-green-900/40 text-green-300">Fertig</span>
									{:else}
										<XCircle size={15} class="text-red-400 shrink-0" />
										<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-red-900/40 text-red-300">Fehler</span>
									{/if}
								</div>
							</td>

							<!-- Zeitstempel -->
							<td class="py-3 pr-4 whitespace-nowrap">
								<div class="text-xs tabular-nums text-gray-400">{new Date(job.queuedAt).toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' })}</div>
								<div class="text-xs tabular-nums text-gray-500">{new Date(job.queuedAt).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</div>
							</td>

							<!-- SKU -->
							<td class="py-3 pr-4">
								<span class="font-mono font-medium text-white">{displayJobSku(job.sku)}</span>
							</td>

							<!-- Operation -->
							<td class="py-3 pr-4">
								<div class="flex items-center gap-1.5">
									<Zap size={13} class="text-royal-400 shrink-0" />
									<span class="text-gray-300">{operationLabel(job.operation)}</span>
								</div>
							</td>

							<!-- Dauer -->
							<td class="py-3 pr-4 tabular-nums whitespace-nowrap">
								{#if job.status === 'running'}
									<span class="text-royal-300">{elapsedSeconds(job.startedAt ?? job.queuedAt, null)}</span>
								{:else if job.status === 'queued'}
									<span class="text-gray-500 text-xs">—</span>
								{:else}
									<span class="text-gray-400">{elapsedSeconds(job.startedAt ?? job.queuedAt, job.completedAt)}</span>
								{/if}
							</td>

							<!-- Buffer ID -->
							<td class="py-3 pr-4">
								{#if job.bufferId}
									<span class="font-mono text-xs text-gray-400 max-w-[120px] truncate block" title={job.bufferId}>{job.bufferId}</span>
								{:else}
									<span class="text-gray-600">—</span>
								{/if}
							</td>

							<!-- Fehler -->
							<td class="py-3">
								{#if job.error}
									<span class="text-xs text-red-400 max-w-[200px] truncate block" title={job.error}>{job.error}</span>
								{:else}
									<span class="text-gray-600">—</span>
								{/if}
							</td>
						</tr>

						<!-- Log console row -->
						{#if expandedJobId === job.id}
							<tr>
								<td colspan="11" class="pb-3 pt-0">
									<div transition:slide={{ duration: 220, easing: cubicOut }}>
										<div class="mx-1 rounded-lg border border-white/10 bg-gray-950/80 overflow-hidden">
											<!-- Console header -->
											<div class="flex items-center gap-2 px-3 py-2 border-b border-white/10 bg-black/30">
												<Terminal size={13} class="text-royal-400" />
												<span class="text-xs font-mono font-medium text-royal-300">
													Actindo API Log — {displayJobSku(job.sku)}
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
											<div class="text-xs p-3 max-h-72 overflow-y-auto">
												{#if jobLogs.length === 0}
													{#if job.status === 'queued'}
														<p class="text-gray-500 italic font-mono">Wartet auf freien Slot...</p>
													{:else if job.status === 'running'}
														<div class="flex items-center gap-2 text-gray-500 font-mono">
															<Loader2 size={12} class="animate-spin text-royal-500" />
															<span class="italic">Warte auf ersten API-Call...</span>
														</div>
													{:else}
														<p class="text-gray-500 italic font-mono">Keine API-Calls aufgezeichnet.</p>
													{/if}
												{:else}
													<div class="space-y-0.5">
														<!-- Header row -->
														<div class="grid px-2 pb-1 mb-1 -mx-2 border-b border-white/5"
															style="grid-template-columns: 14px 52px 88px minmax(205px, 365px) 180px 80px auto; column-gap: 20px">
															<div></div>
															<span class="text-[10px] font-medium text-gray-600 uppercase tracking-wide">Zeit</span>
															<span class="text-[10px] font-medium text-gray-600 uppercase tracking-wide">Typ</span>
															<span class="text-[10px] font-medium text-gray-600 uppercase tracking-wide">Endpoint</span>
															<span class="text-[10px] font-medium text-gray-600 uppercase tracking-wide">SKU</span>
															<span class="text-[10px] font-medium text-gray-600 uppercase tracking-wide">ID</span>
															<div></div>
														</div>

														{#each jobLogs as entry}
															{@const meta = parseLogEntryMeta(entry, job)}
															<!-- svelte-ignore a11y_click_events_have_key_events a11y_interactive_supports_focus -->
															<div
																class="grid gap-x-3 items-center group cursor-pointer rounded-md px-2 py-1.5 -mx-2 hover:bg-white/5 transition-colors"
																style="grid-template-columns: 14px 52px 88px minmax(205px, 365px) 180px 80px auto; column-gap: 20px"
																onclick={(e) => openPayloadModal(entry, e)}
																title="Klicken für Request/Response Payload"
																role="button"
																tabindex="0"
															>
																<!-- Status icon -->
																<div class="shrink-0">
																	{#if entry.success}
																		<CircleCheck size={12} class="text-green-400" />
																	{:else}
																		<CircleX size={12} class="text-red-400" />
																	{/if}
																</div>

																<!-- Timestamp -->
																<span class="text-gray-600 tabular-nums font-mono text-[11px]">
																	{formatTime(entry.timestamp)}
																</span>

																<!-- Type badge -->
																<div>
																	{#if meta.type === 'master'}
																		<span class="inline-flex text-[10px] font-semibold px-1.5 py-0.5 rounded bg-royal-600/30 text-royal-300 border border-royal-500/20 whitespace-nowrap">
																			Master
																		</span>
																	{:else if meta.type === 'variant'}
																		<span class="inline-flex text-[10px] font-semibold px-1.5 py-0.5 rounded bg-purple-900/40 text-purple-300 border border-purple-500/20 whitespace-nowrap">
																			Variante
																		</span>
																	{:else if meta.type === 'relation'}
																		<span class="inline-flex text-[10px] font-semibold px-1.5 py-0.5 rounded bg-amber-900/30 text-amber-300 border border-amber-500/20 whitespace-nowrap">
																			Verknüpfung
																		</span>
																	{:else if meta.type === 'inventory'}
																		<span class="inline-flex text-[10px] font-semibold px-1.5 py-0.5 rounded bg-yellow-900/40 text-yellow-300 border border-yellow-500/20 whitespace-nowrap">
																			Bestand
																		</span>
																	{:else if meta.type === 'image-file'}
																		<span class="inline-flex text-[10px] font-semibold px-1.5 py-0.5 rounded bg-cyan-900/40 text-cyan-300 border border-cyan-500/20 whitespace-nowrap">
																			Bild
																		</span>
																	{:else if meta.type === 'image-link'}
																		<span class="inline-flex text-[10px] font-semibold px-1.5 py-0.5 rounded bg-sky-900/40 text-sky-300 border border-sky-500/20 whitespace-nowrap">
																			Bild-Link
																		</span>
																	{:else if meta.type === 'nav'}
																		<span class="inline-flex text-[10px] font-semibold px-1.5 py-0.5 rounded bg-emerald-900/40 text-emerald-300 border border-emerald-500/20 whitespace-nowrap">
																			NAV
																		</span>
																	{/if}
																</div>

																<!-- Endpoint (flex, takes all available space) -->
																<span class="font-mono text-[11px] {entry.success ? 'text-gray-400' : 'text-red-400'} truncate min-w-0">
																	{shortEndpoint(entry.endpoint)}
																</span>

																<!-- SKU / primary detail (fixed 180px) -->
																<div class="min-w-0 overflow-hidden">
																	{#if meta.type === 'master' || meta.type === 'variant'}
																		<span class="font-mono text-[11px] {entry.success ? 'text-white/80' : 'text-red-300/70'} truncate block">
																			{meta.sku}
																		</span>
																	{:else if meta.type === 'relation'}
																		<span class="text-[11px] text-gray-400 font-mono truncate block">
																			#{meta.variantId} <span class="text-gray-600">→</span> #{meta.parentId}
																		</span>
																	{:else if meta.type === 'inventory'}
																		<span class="font-mono text-[11px] text-white/80 truncate block">{meta.sku}</span>
																	{:else if meta.type === 'image-file'}
																		<span class="font-mono text-[11px] text-white/80 truncate block" title={meta.productRef}>
																			{meta.productRef}
																		</span>
																		{#if meta.path}
																			<span class="text-[10px] text-cyan-300/70 truncate block" title={meta.path}>
																				{meta.path}
																			</span>
																		{/if}
																	{:else if meta.type === 'image-link'}
																		<span class="font-mono text-[11px] text-white/80 truncate block">{meta.productRef}</span>
																		{#if meta.imageIds.length > 0}
																			<span class="text-[10px] text-sky-300/70 truncate block" title={meta.imageIds.join(', ')}>
																				{meta.imageIds.length} Bild-ID{meta.imageIds.length === 1 ? '' : 's'}
																			</span>
																		{/if}
																	{:else if meta.type === 'nav'}
																		<span class="font-mono text-[11px] text-white/80 truncate block" title={meta.productRef ?? meta.requestType}>
																			{meta.productRef ?? 'NAV'}
																		</span>
																		<span class="text-[10px] text-emerald-300/70 truncate block" title={meta.requestType}>
																			{meta.requestType}
																		</span>
																	{/if}
																	{#if entry.error && (meta.type === 'unknown')}
																		<span class="text-red-400/80 text-[11px] truncate block" title={entry.error}>{entry.error}</span>
																	{/if}
																</div>

																<!-- ID / secondary detail (fixed 80px, always aligned) -->
																<div>
																	{#if (meta.type === 'master' || meta.type === 'variant') && meta.actindoId}
																		<span class="text-[10px] tabular-nums px-1.5 py-0.5 rounded bg-white/5 text-royal-400/80 font-mono border border-white/5 whitespace-nowrap">
																			{meta.actindoId}
																		</span>
																	{:else if meta.type === 'inventory' && meta.warehouseId}
																		<span class="text-[10px] px-1.5 py-0.5 rounded bg-white/5 text-gray-500 font-mono border border-white/5 whitespace-nowrap">
																			{meta.warehouseId}
																		</span>
																	{:else if meta.type === 'nav' && (meta.actindoId || meta.navId)}
																		<span class="text-[10px] px-1.5 py-0.5 rounded bg-white/5 text-emerald-300/80 font-mono border border-white/5 whitespace-nowrap" title={meta.navId ? `NAV: ${meta.navId}` : undefined}>
																			{meta.actindoId ?? meta.navId}
																		</span>
																	{:else if entry.error && meta.type !== 'unknown'}
																		<span class="text-red-400/70 text-[10px] truncate block" title={entry.error}>↳ {entry.error}</span>
																	{/if}
																</div>

																<!-- Click hint -->
																<span class="text-gray-600 group-hover:text-gray-400 text-[10px] opacity-0 group-hover:opacity-100 transition-opacity font-mono whitespace-nowrap">
																	payload
																</span>
															</div>
														{/each}

														{#if job.status === 'running'}
															<div class="flex items-center gap-2 text-gray-500 pt-1 px-2 font-mono">
																<Loader2 size={11} class="animate-spin text-royal-500 shrink-0" />
																<span class="italic text-[11px]">Läuft...</span>
															</div>
														{/if}
													</div>
												{/if}
											</div>
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
			Erfolgreiche Jobs werden nach 5 Tagen automatisch entfernt. Fehlgeschlagene Jobs bleiben dauerhaft. Klick auf eine Zeile für den API-Log.
		</p>
	{/if}
</Card>

<!-- Payload Modal -->
{#if selectedLogEntry}
	<Modal
		bind:open={payloadModalOpen}
		title={shortEndpoint(selectedLogEntry.endpoint)}
		class="max-w-7xl"
		onclose={() => (selectedLogEntry = null)}
	>
		{#snippet headerActions()}
			<button
				type="button"
				onclick={handleReplay}
				disabled={replayLoading}
				class="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium
					bg-royal-600/30 border border-royal-500/40 text-royal-300
					hover:bg-royal-600/50 hover:text-white transition-colors
					disabled:opacity-50 disabled:cursor-not-allowed"
			>
				{#if replayLoading}
					<Loader2 size={12} class="animate-spin" />
					Läuft...
				{:else}
					<Play size={12} />
					Replay
				{/if}
			</button>
		{/snippet}

		<div class="space-y-4">
			<p class="text-xs text-gray-500 font-mono break-all -mt-2">{selectedLogEntry.endpoint}</p>

			<div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
				<div>
					<div class="flex items-center gap-2 mb-2">
						<span class="text-xs font-semibold text-gray-400 uppercase tracking-wide">Request</span>
						<span class="text-xs text-gray-600">· bearbeitbar</span>
					</div>
					<textarea
						bind:value={editablePayload}
						class="w-full text-xs font-mono bg-black/40 border border-white/10 rounded-lg p-3 text-gray-300 whitespace-pre resize-none h-[60vh] focus:outline-none focus:border-royal-500/50"
						spellcheck="false"
					></textarea>
				</div>

				<div>
					<div class="flex items-center gap-2 mb-2">
						<span class="text-xs font-semibold text-gray-400 uppercase tracking-wide">Response</span>
						{#if replaySuccess === null && !selectedLogEntry.success}
							<span class="text-xs px-1.5 py-0.5 rounded bg-red-900/40 text-red-400">Fehler</span>
						{:else if replaySuccess === true}
							<span class="text-xs px-1.5 py-0.5 rounded bg-green-900/40 text-green-400">Replay OK</span>
						{:else if replaySuccess === false}
							<span class="text-xs px-1.5 py-0.5 rounded bg-red-900/40 text-red-400">Replay Fehler</span>
						{/if}
					</div>
					{#if replayError}
						<div class="text-xs font-mono bg-black/40 border border-red-500/30 rounded-lg p-3 text-red-400 h-[60vh] overflow-y-auto whitespace-pre-wrap">
							{replayError}
						</div>
					{:else}
						<pre class="text-xs font-mono bg-black/40 border border-white/10 rounded-lg p-3 overflow-auto h-[60vh] {replaySuccess === false ? 'text-red-300' : replaySuccess === true ? 'text-green-200' : selectedLogEntry.success ? 'text-gray-300' : 'text-red-300'} whitespace-pre">{replaySuccess !== null ? (replayResponsePayload ?? '—') : formatJson(selectedLogEntry.responsePayload)}</pre>
					{/if}
				</div>
			</div>
		</div>
	</Modal>
{/if}

{#if navPayloadModalOpen && !navPayloadJob}
	<Modal
		bind:open={navPayloadModalOpen}
		title="NAV Request"
		class="max-w-3xl"
		onclose={() => (navPayloadJob = null)}
	>
		<div class="py-10 flex items-center justify-center">
			<div class="flex items-center gap-3 text-sm text-gray-400">
				<Loader2 size={18} class={navPayloadLoading ? 'animate-spin' : ''} />
				<span>{navPayloadLoading ? 'Lade Job-Details...' : 'Keine NAV-Daten verfÃ¼gbar.'}</span>
			</div>
		</div>
	</Modal>
{/if}

{#if newJobModalOpen}
	<Modal
		bind:open={newJobModalOpen}
		title="Neuer Job"
		class="max-w-5xl"
	>
		{#snippet headerActions()}
			<button
				type="button"
				onclick={handleNewJobSubmit}
				disabled={replayLoading || !newJobEndpoint.trim()}
				class="flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium
					bg-royal-600/30 border border-royal-500/40 text-royal-300
					hover:bg-royal-600/50 hover:text-white transition-colors
					disabled:opacity-50 disabled:cursor-not-allowed"
			>
				{#if replayLoading}
					<Loader2 size={12} class="animate-spin" />
					Läuft...
				{:else}
					<Play size={12} />
					Senden
				{/if}
			</button>
		{/snippet}

		<div class="space-y-4">
			<div>
				<label for="job-endpoint" class="block text-xs font-semibold text-gray-400 uppercase tracking-wide mb-2">
					Endpoint
				</label>
				<select
					id="job-endpoint"
					bind:value={newJobEndpoint}
					class="w-full bg-black/40 border border-white/10 rounded-lg px-3 py-2 text-sm text-gray-300 focus:outline-none focus:border-royal-500/50"
				>
					{#if availableEndpoints.length === 0}
						<option value="">Keine gepflegten Endpoints gefunden</option>
					{:else}
						{#each availableEndpoints as endpoint}
							<option value={endpoint.value}>
								{endpoint.key} · {endpoint.value}
							</option>
						{/each}
					{/if}
				</select>
				<p class="mt-2 text-xs text-gray-500">Der Request wird immer als POST gesendet.</p>
			</div>

			<div>
				<label for="job-payload" class="block text-xs font-semibold text-gray-400 uppercase tracking-wide mb-2">
					Payload
				</label>
				<textarea
					id="job-payload"
					bind:value={newJobPayload}
					class="w-full text-xs font-mono bg-black/40 border border-white/10 rounded-lg p-3 text-gray-300 whitespace-pre resize-none h-[40vh] focus:outline-none focus:border-royal-500/50"
					spellcheck="false"
				></textarea>
			</div>

			<div>
				<div class="flex items-center gap-2 mb-2">
					<span class="text-xs font-semibold text-gray-400 uppercase tracking-wide">Response</span>
					{#if replaySuccess === true}
						<span class="text-xs px-1.5 py-0.5 rounded bg-green-900/40 text-green-400">OK</span>
					{:else if replaySuccess === false}
						<span class="text-xs px-1.5 py-0.5 rounded bg-red-900/40 text-red-400">Fehler</span>
					{/if}
				</div>
				{#if replayError}
					<div class="text-xs font-mono bg-black/40 border border-red-500/30 rounded-lg p-3 text-red-400 min-h-[120px] overflow-y-auto whitespace-pre-wrap">
						{replayError}
					</div>
				{:else}
					<pre class="text-xs font-mono bg-black/40 border border-white/10 rounded-lg p-3 overflow-auto min-h-[120px] {replaySuccess === false ? 'text-red-300' : replaySuccess === true ? 'text-green-200' : 'text-gray-500'} whitespace-pre">{replayResponsePayload ?? 'Noch keine Antwort.'}</pre>
				{/if}
			</div>
		</div>
	</Modal>
{/if}

<!-- NAV Payload Modal -->
{#if navPayloadJob}
	<Modal
		bind:open={navPayloadModalOpen}
		title="NAV Request — {navPayloadJob.sku}"
		class="max-w-7xl"
		onclose={() => (navPayloadJob = null)}
	>
		<div class="space-y-4">
			<div class="flex items-center gap-3 text-xs text-gray-500 -mt-2">
				<span class="font-mono">{operationLabel(navPayloadJob.operation)}</span>
				<span>·</span>
				<span class="font-mono">{new Date(navPayloadJob.queuedAt).toLocaleString('de-DE')}</span>
			</div>
			<div class="grid grid-cols-1 lg:grid-cols-2 gap-4">
				<div>
					<div class="flex items-center gap-2 mb-2">
						<span class="text-xs font-semibold text-gray-400 uppercase tracking-wide">NAV → Middleware Request</span>
					</div>
					<pre class="text-xs font-mono bg-black/40 border border-white/10 rounded-lg p-3 overflow-auto h-[60vh] text-gray-300 whitespace-pre">{formatJson(navPayloadJob.navRequestPayload)}</pre>
				</div>
				<div>
					<div class="flex items-center gap-2 mb-2">
						<span class="text-xs font-semibold text-gray-400 uppercase tracking-wide">Middleware → NAV Response</span>
					</div>
					<pre class="text-xs font-mono bg-black/40 border border-white/10 rounded-lg p-3 overflow-auto h-[60vh] text-gray-300 whitespace-pre">{navPayloadJob.navResponsePayload ? formatJson(navPayloadJob.navResponsePayload) : '—'}</pre>
				</div>
			</div>
		</div>
	</Modal>
{/if}
