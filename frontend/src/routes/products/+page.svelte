<script lang="ts">
	import { onMount } from 'svelte';
	import { RefreshCw, Search, Package, ChevronDown, ChevronRight, X, Warehouse, ExternalLink, AlertTriangle, ChevronRight as ChevronRightIcon, ArrowUpToLine, Loader2 } from 'lucide-svelte';
	import { products as productsApi, settings as settingsApi, sync as syncApi } from '$api/client';
	import type { ProductListItem, ProductStockItem, NavSyncErrorsDto, NavSyncMissingItem } from '$api/types';
	import { formatDate } from '$utils/format';
	import PageHeader from '$components/layout/PageHeader.svelte';
	import Card from '$components/ui/Card.svelte';
	import Button from '$components/ui/Button.svelte';
	import Input from '$components/ui/Input.svelte';
	import Badge from '$components/ui/Badge.svelte';
	import Alert from '$components/ui/Alert.svelte';
	import Spinner from '$components/ui/Spinner.svelte';

	function formatPrice(price: number | null): string {
		if (price === null) return '-';
		return new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR' }).format(price);
	}

	let products: ProductListItem[] = $state([]);
	let loading = $state(true);
	let error = $state('');
	let search = $state('');
	// Tabs
	type Tab = 'products' | 'sync-errors';
	let activeTab = $state<Tab>('products');

	const DEFAULT_ACTINDO_BASE_URL = 'https://schalke-dev.dev.actindo.com';
	let actindoBaseUrl = $state(DEFAULT_ACTINDO_BASE_URL);

	// NAV Sync Errors tab
	let syncErrors = $state<NavSyncErrorsDto | null>(null);
	let syncErrorsLoading = $state(false);
	let syncErrorsError = $state('');
	let syncErrorsExpandedSkus = $state<Set<string>>(new Set());

	async function loadSyncErrors() {
		syncErrorsLoading = true;
		syncErrorsError = '';
		try {
			syncErrors = await productsApi.navSyncErrors();
		} catch (err) {
			syncErrorsError = err instanceof Error ? err.message : 'Fehler beim Laden';
		} finally {
			syncErrorsLoading = false;
		}
	}

	function toggleSyncErrorExpand(sku: string) {
		const next = new Set(syncErrorsExpandedSkus);
		if (next.has(sku)) next.delete(sku);
		else next.add(sku);
		syncErrorsExpandedSkus = next;
	}

	// Toast notifications
	interface Toast { id: number; message: string; }
	let toasts = $state<Toast[]>([]);
	let _toastId = 0;

	function showErrorToast(message: string) {
		const id = ++_toastId;
		toasts = [...toasts, { id, message }];
		setTimeout(() => { toasts = toasts.filter((t) => t.id !== id); }, 5000);
	}

	let fixingSkus = $state<Set<string>>(new Set());

	async function fixNavId(sku: string, e: MouseEvent) {
		e.stopPropagation();
		if (fixingSkus.has(sku)) return;
		fixingSkus = new Set([...fixingSkus, sku]);
		try {
			await syncApi.forceSyncProducts([sku]);
			await loadSyncErrors();
		} catch (err) {
			const msg = err instanceof Error ? err.message : 'Unbekannter Fehler';
			showErrorToast(`ID konnte nicht gesetzt werden: ${msg}`);
		} finally {
			const next = new Set(fixingSkus);
			next.delete(sku);
			fixingSkus = next;
		}
	}

	function switchTab(tab: Tab) {
		activeTab = tab;
		if (tab === 'sync-errors' && syncErrors === null && !syncErrorsLoading) {
			loadSyncErrors();
		}
	}

	// Expanded master products (SKU -> variants)
	let expandedProducts: Record<string, ProductListItem[]> = $state({});
	let loadingVariants: Record<string, boolean> = $state({});

	// Stock modal state
	let stockModalOpen = $state(false);
	let stockModalSku = $state('');
	let stockModalLoading = $state(false);
	let stockModalStocks: ProductStockItem[] = $state([]);
	let stockModalTotal = $derived(stockModalStocks.reduce((sum, s) => sum + s.stock, 0));

	async function openStockModal(sku: string) {
		stockModalSku = sku;
		stockModalOpen = true;
		stockModalLoading = true;
		stockModalStocks = [];
		try {
			stockModalStocks = await productsApi.getStocks(sku);
		} catch (err) {
			console.error('Failed to load stocks:', err);
		} finally {
			stockModalLoading = false;
		}
	}

	function closeStockModal() {
		stockModalOpen = false;
		stockModalSku = '';
		stockModalStocks = [];
	}

	let filteredProducts = $derived(
		search.trim()
			? products.filter(
					(p) =>
						p.sku.toLowerCase().includes(search.toLowerCase()) ||
						p.name.toLowerCase().includes(search.toLowerCase()) ||
						(p.variantCode && p.variantCode.toLowerCase().includes(search.toLowerCase()))
				)
			: products
	);

	function actindoProductUrl(sku: string): string {
		const base = actindoBaseUrl.replace(/\/$/, '');
		return `${base}/Actindo.CoreModules.Start.Start.start#/Actindo.Modules.Actindo.PIM.Views.start/products/list/${sku}`;
	}

	function syncStatusLabel(product: ProductListItem): string {
		const dates = [product.lastPriceUpdatedAt, product.lastStockUpdatedAt].filter(Boolean);
		if (dates.length === 0) return '';
		const latest = dates.reduce((a, b) => (a! > b! ? a : b))!;
		const diff = Date.now() - new Date(latest).getTime();
		const hours = Math.floor(diff / 3600000);
		if (hours < 1) return 'Vor < 1h';
		if (hours < 24) return `Vor ${hours}h`;
		const days = Math.floor(hours / 24);
		return `Vor ${days}T`;
	}

	onMount(() => {
		loadProducts();
		settingsApi.getActindoBaseUrl()
			.then((r) => { if (r.actindoBaseUrl) actindoBaseUrl = r.actindoBaseUrl; })
			.catch(() => {});
	});

	async function loadProducts() {
		loading = true;
		error = '';
		expandedProducts = {};
		try {
			products = await productsApi.list();
		} catch (err) {
			error = err instanceof Error ? err.message : 'Fehler beim Laden';
		} finally {
			loading = false;
		}
	}

	async function toggleVariants(masterSku: string) {
		if (expandedProducts[masterSku]) {
			// Collapse
			const { [masterSku]: _, ...rest } = expandedProducts;
			expandedProducts = rest;
		} else {
			// Expand - load variants
			loadingVariants = { ...loadingVariants, [masterSku]: true };
			try {
				const variants = await productsApi.getVariants(masterSku);
				expandedProducts = { ...expandedProducts, [masterSku]: variants };
			} catch (err) {
				console.error('Failed to load variants:', err);
			} finally {
				const { [masterSku]: _, ...rest } = loadingVariants;
				loadingVariants = rest;
			}
		}
	}

	function getVariantStatusBadge(status: string) {
		switch (status) {
			case 'master':
				return { variant: 'primary' as const, label: 'Master' };
			case 'child':
				return { variant: 'default' as const, label: 'Variante' };
			case 'single':
			default:
				return { variant: 'default' as const, label: 'Single' };
		}
	}
</script>

<svelte:head>
	<title>Products | Actindo Middleware</title>
</svelte:head>

<PageHeader title="Produkte" subtitle="Uebersicht aller erstellten Produkte">
	{#snippet actions()}
		{#if activeTab === 'products'}
			<Button variant="ghost" onclick={loadProducts} disabled={loading}>
				<RefreshCw size={16} class={loading ? 'animate-spin' : ''} />
				Aktualisieren
			</Button>
		{:else}
			<Button variant="ghost" onclick={loadSyncErrors} disabled={syncErrorsLoading}>
				<RefreshCw size={16} class={syncErrorsLoading ? 'animate-spin' : ''} />
				Aktualisieren
			</Button>
		{/if}
	{/snippet}
</PageHeader>

<!-- Tab switcher -->
<div class="flex gap-1 mb-6 p-1 rounded-xl bg-white/5 border border-white/10 w-fit">
	<button
		type="button"
		onclick={() => switchTab('products')}
		class="px-4 py-1.5 rounded-lg text-sm font-medium transition-colors
			{activeTab === 'products'
			? 'bg-royal-600/40 border border-royal-500/40 text-white'
			: 'text-gray-400 hover:text-gray-200'}"
	>
		Alle Produkte
		{#if products.length > 0}
			<span class="ml-1.5 text-xs opacity-60">{products.length}</span>
		{/if}
	</button>
	<button
		type="button"
		onclick={() => switchTab('sync-errors')}
		class="flex items-center gap-1.5 px-4 py-1.5 rounded-lg text-sm font-medium transition-colors
			{activeTab === 'sync-errors'
			? 'bg-red-900/40 border border-red-500/40 text-red-200'
			: 'text-gray-400 hover:text-gray-200'}"
	>
		{#if syncErrorsLoading}
			<Loader2 size={14} class="animate-spin" />
		{:else}
			<AlertTriangle size={14} />
		{/if}
		NAV Sync-Fehler
		{#if syncErrors && syncErrors.missingFromNav > 0}
			<span class="ml-0.5 text-xs px-1.5 py-0.5 rounded-full bg-red-900/60 text-red-300 border border-red-500/30">
				{syncErrors.missingFromNav}
			</span>
		{/if}
	</button>
</div>

{#if error}
	<Alert variant="error" class="mb-6">{error}</Alert>
{/if}

{#if activeTab === 'products'}
<Card>
	<!-- Search -->
	<div class="mb-6">
		<div class="relative max-w-md">
			<Search size={18} class="absolute left-4 top-1/2 -translate-y-1/2 text-gray-500" />
			<Input
				type="search"
				placeholder="Suche nach SKU, Name oder Variantencode..."
				bind:value={search}
				class="pl-11"
			/>
		</div>
	</div>

	{#if loading && products.length === 0}
		<div class="flex justify-center py-12">
			<Spinner />
		</div>
	{:else if filteredProducts.length === 0}
		<div class="text-center py-12 text-gray-400">
			<Package size={48} class="mx-auto mb-4 opacity-50" />
			<p>{search ? 'Keine Produkte gefunden' : 'Noch keine Produkte vorhanden'}</p>
		</div>
	{:else}
		<div class="overflow-x-auto">
			<table class="w-full">
				<thead>
					<tr class="border-b border-white/10">
						<th
							class="text-left py-3 px-4 text-xs uppercase tracking-wider text-gray-400 font-medium w-10"
						>
						</th>
						<th
							class="text-left py-3 px-4 text-xs uppercase tracking-wider text-gray-400 font-medium"
						>
							SKU
						</th>
						<th
							class="text-left py-3 px-4 text-xs uppercase tracking-wider text-gray-400 font-medium"
						>
							Name
						</th>
						<th
							class="text-left py-3 px-4 text-xs uppercase tracking-wider text-gray-400 font-medium"
						>
							Status
						</th>
						<th
							class="text-left py-3 px-4 text-xs uppercase tracking-wider text-gray-400 font-medium"
						>
							Actindo ID
						</th>
						<th
							class="text-right py-3 px-4 text-xs uppercase tracking-wider text-gray-400 font-medium"
						>
							Preis
						</th>
						<th
							class="text-right py-3 px-4 text-xs uppercase tracking-wider text-gray-400 font-medium"
						>
							Bestand
						</th>
						<th
							class="text-left py-3 px-4 text-xs uppercase tracking-wider text-gray-400 font-medium"
						>
							Sync
						</th>
						<th
							class="text-left py-3 px-4 text-xs uppercase tracking-wider text-gray-400 font-medium"
						>
							Erstellt
						</th>
					</tr>
				</thead>
				<tbody>
					{#each filteredProducts as product}
						{@const isExpanded = !!expandedProducts[product.sku]}
						{@const isLoading = !!loadingVariants[product.sku]}
						{@const isMaster = product.variantStatus === 'master'}
						{@const statusBadge = getVariantStatusBadge(product.variantStatus)}

						<tr class="border-b border-white/5 hover:bg-white/5 transition-colors">
							<!-- Expand Button -->
							<td class="py-3 px-4">
								{#if isMaster && product.variantCount && product.variantCount > 0}
									<button
										type="button"
										onclick={() => toggleVariants(product.sku)}
										class="p-1 rounded hover:bg-white/10 transition-colors"
										disabled={isLoading}
									>
										{#if isLoading}
											<RefreshCw size={16} class="animate-spin text-gray-400" />
										{:else if isExpanded}
											<ChevronDown size={16} class="text-royal-400" />
										{:else}
											<ChevronRight size={16} class="text-gray-400" />
										{/if}
									</button>
								{/if}
							</td>

							<!-- SKU -->
							<td class="py-3 px-4">
								<div class="flex items-center gap-1.5">
									<span
										class="font-mono text-sm {isMaster
											? 'text-royal-300 font-semibold'
											: 'text-royal-300'}"
									>
										{product.sku}
									</span>
									<a
										href={actindoProductUrl(product.sku)}
										target="_blank"
										rel="noopener noreferrer"
										onclick={(e) => e.stopPropagation()}
										class="text-gray-600 hover:text-royal-400 transition-colors"
										title="In Actindo öffnen"
									>
										<ExternalLink size={12} />
									</a>
								</div>
							</td>

							<!-- Name -->
							<td class="py-3 px-4">
								{product.name || '-'}
							</td>

							<!-- Status -->
							<td class="py-3 px-4">
								<div class="flex items-center gap-2">
									<Badge variant={statusBadge.variant}>{statusBadge.label}</Badge>
									{#if isMaster && product.variantCount}
										<Badge variant="info">{product.variantCount} Varianten</Badge>
									{/if}
								</div>
							</td>

							<!-- Actindo ID -->
							<td class="py-3 px-4">
								{#if product.productId}
									<span class="font-mono text-sm">{product.productId}</span>
								{:else}
									<span class="text-gray-500">-</span>
								{/if}
							</td>

							<!-- Preis -->
							<td class="py-3 px-4 text-right">
								{#if product.lastPrice !== null}
									<span class="font-mono text-sm text-green-400">{formatPrice(product.lastPrice)}</span>
									{#if product.lastPriceEmployee || product.lastPriceMember}
										<div class="text-xs text-gray-500 mt-0.5">
											{#if product.lastPriceEmployee}MA: {formatPrice(product.lastPriceEmployee)}{/if}
											{#if product.lastPriceMember}{product.lastPriceEmployee ? ' / ' : ''}Mit: {formatPrice(product.lastPriceMember)}{/if}
										</div>
									{/if}
								{:else}
									<span class="text-gray-500">-</span>
								{/if}
							</td>

							<!-- Bestand -->
							<td class="py-3 px-4 text-right">
								{#if product.lastStock !== null}
									<button
										type="button"
										onclick={() => openStockModal(product.sku)}
										class="font-mono text-sm {product.lastStock > 0 ? 'text-blue-400' : 'text-red-400'} underline decoration-dotted underline-offset-2 hover:decoration-solid cursor-pointer"
									>
										{product.lastStock}
									</button>
								{:else}
									<span class="text-gray-500">-</span>
								{/if}
							</td>

							<!-- Sync Status -->
							<td class="py-3 px-4 text-sm text-gray-500">
								{#if syncStatusLabel(product)}
									<span class="text-xs text-gray-500" title="Zuletzt synchronisiert">{syncStatusLabel(product)}</span>
								{:else}
									<span class="text-gray-600">—</span>
								{/if}
							</td>

							<!-- Erstellt -->
							<td class="py-3 px-4 text-sm text-gray-400">
								{formatDate(product.createdAt)}
							</td>
						</tr>

						<!-- Expanded Variants -->
						{#if isExpanded && expandedProducts[product.sku]}
							{#each expandedProducts[product.sku] as variant}
								<tr class="border-b border-white/5 bg-royal-900/20">
									<td class="py-2 px-4"></td>
									<td class="py-2 px-4">
										<div class="flex items-center gap-1.5">
											<span class="font-mono text-sm text-gray-400 pl-4 whitespace-nowrap">
												<span class="text-royal-600 mr-1">└</span>
												{variant.sku}
											</span>
											<a
												href={actindoProductUrl(variant.sku)}
												target="_blank"
												rel="noopener noreferrer"
												onclick={(e) => e.stopPropagation()}
												class="text-gray-600 hover:text-royal-400 transition-colors"
												title="In Actindo öffnen"
											>
												<ExternalLink size={12} />
											</a>
										</div>
									</td>
									<td class="py-2 px-4">
										{#if variant.variantCode}
											<Badge variant="default" class="font-mono text-xs"
												>{variant.variantCode}</Badge
											>
										{:else}
											<span class="text-gray-500">-</span>
										{/if}
									</td>
									<td class="py-2 px-4">
										<Badge variant="default">Variante</Badge>
									</td>
									<td class="py-2 px-4">
										{#if variant.productId}
											<span class="font-mono text-sm">{variant.productId}</span>
										{:else}
											<span class="text-gray-500">-</span>
										{/if}
									</td>
									<td class="py-2 px-4 text-right">
										{#if variant.lastPrice !== null}
											<span class="font-mono text-sm text-green-400">{formatPrice(variant.lastPrice)}</span>
										{:else}
											<span class="text-gray-500">-</span>
										{/if}
									</td>
									<td class="py-2 px-4 text-right">
										{#if variant.lastStock !== null}
											<button
												type="button"
												onclick={() => openStockModal(variant.sku)}
												class="font-mono text-sm {variant.lastStock > 0 ? 'text-blue-400' : 'text-red-400'} underline decoration-dotted underline-offset-2 hover:decoration-solid cursor-pointer"
											>
												{variant.lastStock}
											</button>
										{:else}
											<span class="text-gray-500">-</span>
										{/if}
									</td>
									<!-- Sync Status (empty for variants) -->
									<td class="py-2 px-4"></td>
									<td class="py-2 px-4 text-sm text-gray-400">
										{formatDate(variant.createdAt)}
									</td>
								</tr>
							{/each}
						{/if}
					{/each}
				</tbody>
			</table>
		</div>

		<div class="mt-4 pt-4 border-t border-white/10 text-sm text-gray-400">
			<span>{filteredProducts.length} Produkte</span>
		</div>
	{/if}
</Card>

{:else}

<!-- ── NAV Sync-Fehler Tab ───────────────────────────────────── -->
<Card>
	{#if syncErrorsLoading && !syncErrors}
		<div class="flex justify-center py-12">
			<Spinner />
		</div>
	{:else if syncErrorsError}
		<Alert variant="error">{syncErrorsError}</Alert>
	{:else if !syncErrors}
		<div class="text-center py-12 text-gray-400">
			<AlertTriangle size={48} class="mx-auto mb-4 opacity-40" />
			<p>Noch keine Daten geladen</p>
		</div>
	{:else if syncErrors.missingFromNav === 0}
		<div class="text-center py-12 text-gray-400">
			<Package size={48} class="mx-auto mb-4 opacity-40" />
			<p class="font-medium text-green-400 mb-1">Alles synchronisiert</p>
			<p class="text-sm text-gray-500">Alle {syncErrors.totalInActindo} Actindo-Produkte sind in NAV eingetragen</p>
		</div>
	{:else}
		<!-- Summary -->
		<div class="flex items-center gap-4 mb-5 pb-4 border-b border-white/10">
			<div class="flex items-center gap-2">
				<AlertTriangle size={16} class="text-red-400" />
				<span class="text-sm font-medium text-red-300">{syncErrors.missingFromNav} Produkte fehlen in NAV</span>
			</div>
			<span class="text-gray-600">·</span>
			<span class="text-sm text-gray-500">{syncErrors.totalInActindo} Produkte in Actindo gesamt</span>
		</div>

		<div class="overflow-x-auto">
			<table class="w-full text-sm">
				<thead>
					<tr class="border-b border-white/10 text-left">
						<th class="pb-3 pr-2 w-6"></th>
						<th class="pb-3 pr-4 font-medium text-gray-400 text-xs uppercase tracking-wider">SKU</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 text-xs uppercase tracking-wider">Actindo ID (Soll)</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 text-xs uppercase tracking-wider">NAV hat</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 text-xs uppercase tracking-wider">Typ</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 text-xs uppercase tracking-wider">Problem</th>
						<th class="pb-3 pr-4 font-medium text-gray-400 text-xs uppercase tracking-wider">Varianten</th>
						<th class="pb-3"></th>
					</tr>
				</thead>
				<tbody class="divide-y divide-white/5">
					{#each syncErrors.items as item (item.sku)}
						{@const isExpanded = syncErrorsExpandedSkus.has(item.sku)}
						{@const hasProblemVariants = item.missingVariants.length > 0}
						{@const isMismatch = item.status === 'mismatch'}
						<tr
							class="hover:bg-white/5 transition-colors
								{isMismatch ? 'bg-amber-900/5' : ''}
								{hasProblemVariants ? 'cursor-pointer' : ''}"
							onclick={() => hasProblemVariants && toggleSyncErrorExpand(item.sku)}
						>
							<!-- Expand toggle -->
							<td class="py-3 pr-2 text-gray-500">
								{#if hasProblemVariants}
									<div class="transition-transform duration-150 {isExpanded ? 'rotate-90' : ''}">
										<ChevronRight size={14} />
									</div>
								{/if}
							</td>

							<!-- SKU -->
							<td class="py-3 pr-4">
								<div class="flex items-center gap-1.5">
									<span class="font-mono text-sm font-semibold text-royal-300">{item.sku}</span>
									<a
										href={actindoProductUrl(item.sku)}
										target="_blank"
										rel="noopener noreferrer"
										onclick={(e) => e.stopPropagation()}
										class="text-gray-600 hover:text-royal-400 transition-colors"
										title="In Actindo öffnen"
									>
										<ExternalLink size={12} />
									</a>
								</div>
							</td>

							<!-- Actindo ID (Soll) -->
							<td class="py-3 pr-4">
								<span class="font-mono text-sm text-green-400">{item.actindoId}</span>
							</td>

							<!-- NAV hat -->
							<td class="py-3 pr-4">
								{#if item.status === 'missing'}
									<span class="text-xs text-gray-600 italic">—</span>
								{:else if item.status === 'mismatch'}
									<span class="font-mono text-sm text-amber-400">{item.navActindoId}</span>
								{:else}
									<span class="text-xs text-gray-600">—</span>
								{/if}
							</td>

							<!-- Typ -->
							<td class="py-3 pr-4">
								{#if item.variantStatus === 'master'}
									<Badge variant="primary">Master</Badge>
								{:else}
									<Badge variant="default">Single</Badge>
								{/if}
							</td>

							<!-- Problem -->
							<td class="py-3 pr-4">
								{#if item.status === 'missing'}
									<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-red-900/40 border border-red-500/30 text-red-300">
										Fehlt in NAV
									</span>
								{:else if item.status === 'mismatch'}
									<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-amber-900/40 border border-amber-500/30 text-amber-300">
										ID-Konflikt
									</span>
								{:else}
									<span class="text-xs text-gray-500">Varianten</span>
								{/if}
							</td>

							<!-- Varianten-Probleme -->
							<td class="py-3 pr-4">
								{#if item.variantStatus === 'master' && hasProblemVariants}
									{@const missingCount = item.missingVariants.filter(v => v.status === 'missing').length}
									{@const mismatchCount = item.missingVariants.filter(v => v.status === 'mismatch').length}
									<div class="flex flex-wrap gap-1">
										{#if missingCount > 0}
											<span class="text-xs text-red-400">{missingCount} fehlend</span>
										{/if}
										{#if mismatchCount > 0}
											<span class="text-xs text-amber-400">{mismatchCount} Konflikt</span>
										{/if}
										<span class="text-xs text-gray-600">/ {item.totalVariants}</span>
									</div>
								{:else}
									<span class="text-gray-600">—</span>
								{/if}
							</td>

							<!-- Fix button -->
							<td class="py-3">
								<button
									type="button"
									onclick={(e) => fixNavId(item.sku, e)}
									disabled={fixingSkus.has(item.sku)}
									class="flex items-center gap-1.5 px-2.5 py-1 rounded-lg text-xs font-medium
										bg-royal-600/20 border border-royal-500/30 text-royal-300
										hover:bg-royal-600/40 hover:text-white transition-colors
										disabled:opacity-40 disabled:cursor-not-allowed whitespace-nowrap"
									title="Actindo ID in NAV schreiben"
								>
									{#if fixingSkus.has(item.sku)}
										<Loader2 size={12} class="animate-spin" />
										Setze...
									{:else}
										<ArrowUpToLine size={12} />
										ID in NAV setzen
									{/if}
								</button>
							</td>
						</tr>

						<!-- Expanded variant problems -->
						{#if isExpanded && hasProblemVariants}
							{#each item.missingVariants as variant (variant.sku)}
								<tr class="{variant.status === 'mismatch' ? 'bg-amber-900/10' : 'bg-red-900/10'} border-b border-white/5">
									<td class="py-2 pr-2"></td>
									<td class="py-2 pr-4">
										<div class="flex items-center gap-1.5 pl-4">
											<span class="{variant.status === 'mismatch' ? 'text-amber-700' : 'text-red-700'} mr-1">└</span>
											<span class="font-mono text-sm text-gray-400">{variant.sku}</span>
											<a
												href={actindoProductUrl(variant.sku)}
												target="_blank"
												rel="noopener noreferrer"
												onclick={(e) => e.stopPropagation()}
												class="text-gray-600 hover:text-royal-400 transition-colors"
												title="In Actindo öffnen"
											>
												<ExternalLink size={11} />
											</a>
										</div>
									</td>
									<td class="py-2 pr-4">
										<span class="font-mono text-sm text-green-400">{variant.actindoId}</span>
									</td>
									<td class="py-2 pr-4">
										{#if variant.status === 'mismatch'}
											<span class="font-mono text-sm text-amber-400">{variant.navActindoId}</span>
										{:else}
											<span class="text-xs text-gray-600 italic">—</span>
										{/if}
									</td>
									<td class="py-2 pr-4">
										<Badge variant="default">Variante</Badge>
									</td>
									<td class="py-2 pr-4">
										{#if variant.status === 'mismatch'}
											<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-amber-900/40 border border-amber-500/30 text-amber-300">
												ID-Konflikt
											</span>
										{:else}
											<span class="text-xs font-medium px-2 py-0.5 rounded-full bg-red-900/40 border border-red-500/30 text-red-300">
												Fehlt in NAV
											</span>
										{/if}
									</td>
									<td class="py-2"></td>
									<td class="py-2"></td>
								</tr>
							{/each}
						{/if}
					{/each}
				</tbody>
			</table>
		</div>
	{/if}
</Card>

{/if}

<!-- Stock Modal -->
{#if stockModalOpen}
	<!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
	<div
		class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm"
		onclick={closeStockModal}
	>
		<!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
		<div
			class="bg-gray-800 rounded-xl border border-white/10 shadow-2xl w-full max-w-md mx-4"
			onclick={(e) => e.stopPropagation()}
		>
			<!-- Header -->
			<div class="flex items-center justify-between p-4 border-b border-white/10">
				<div class="flex items-center gap-3">
					<Warehouse size={20} class="text-blue-400" />
					<div>
						<h3 class="font-semibold">Lagerbestände</h3>
						<p class="text-sm text-gray-400 font-mono">{stockModalSku}</p>
					</div>
				</div>
				<button
					type="button"
					onclick={closeStockModal}
					class="p-1 rounded hover:bg-white/10 transition-colors"
				>
					<X size={20} class="text-gray-400" />
				</button>
			</div>

			<!-- Content -->
			<div class="p-4">
				{#if stockModalLoading}
					<div class="flex justify-center py-8">
						<Spinner />
					</div>
				{:else if stockModalStocks.length === 0}
					<div class="text-center py-8 text-gray-400">
						<Warehouse size={32} class="mx-auto mb-2 opacity-50" />
						<p>Keine Lagerbestände vorhanden</p>
					</div>
				{:else}
					<div class="max-h-80 overflow-y-auto">
						<table class="w-full">
							<thead class="sticky top-0 bg-gray-800">
								<tr class="border-b border-white/10">
									<th class="text-left py-2 text-xs uppercase tracking-wider text-gray-400 font-medium">
										Lager ID
									</th>
									<th class="text-right py-2 text-xs uppercase tracking-wider text-gray-400 font-medium">
										Bestand
									</th>
									<th class="text-right py-2 text-xs uppercase tracking-wider text-gray-400 font-medium">
										Aktualisiert
									</th>
								</tr>
							</thead>
							<tbody>
								{#each stockModalStocks as stock}
									<tr class="border-b border-white/5">
										<td class="py-2">
											<span class="font-mono text-sm">{stock.warehouseId}</span>
										</td>
										<td class="py-2 text-right">
											<span class="font-mono text-sm {stock.stock > 0 ? 'text-blue-400' : 'text-red-400'}">
												{stock.stock}
											</span>
										</td>
										<td class="py-2 text-right text-sm text-gray-400">
											{formatDate(stock.updatedAt)}
										</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
					<!-- Footer outside scrollable area -->
					<div class="border-t border-white/10 mt-2 pt-2">
						<div class="flex justify-between items-center">
							<span class="font-semibold">Gesamt</span>
							<span class="font-mono text-sm font-semibold {stockModalTotal > 0 ? 'text-blue-400' : 'text-red-400'}">
								{stockModalTotal}
							</span>
						</div>
					</div>
				{/if}
			</div>
		</div>
	</div>
{/if}

<!-- Toast Notifications -->
{#if toasts.length > 0}
	<div class="fixed top-4 right-4 z-[100] flex flex-col gap-2 pointer-events-none">
		{#each toasts as toast (toast.id)}
			<div class="flex items-start gap-3 px-4 py-3 rounded-xl shadow-2xl
				bg-gray-900 border border-red-500/40 text-red-300
				min-w-[280px] max-w-[420px] pointer-events-auto">
				<AlertTriangle size={16} class="text-red-400 shrink-0 mt-0.5" />
				<p class="text-sm leading-snug">{toast.message}</p>
			</div>
		{/each}
	</div>
{/if}
