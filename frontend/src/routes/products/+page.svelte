<script lang="ts">
	import { onMount } from 'svelte';
	import { RefreshCw, Search, Package, ChevronDown, ChevronRight, X, Warehouse, ExternalLink } from 'lucide-svelte';
	import { products as productsApi, settings as settingsApi } from '$api/client';
	import type { ProductListItem, ProductStockItem } from '$api/types';
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
	let actindoBaseUrl = $state<string | null>(null);

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

	function actindoProductUrl(productId: number | null): string | null {
		if (!actindoBaseUrl || !productId) return null;
		return `${actindoBaseUrl}/Actindo.CoreModules.Start.Start.start#/Actindo.Modules.Actindo.PIM.Views.start/products/list/${productId}`;
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
		settingsApi.getActindoBaseUrl().then((r) => (actindoBaseUrl = r.actindoBaseUrl)).catch(() => {});
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
		<Button variant="ghost" onclick={loadProducts} disabled={loading}>
			<RefreshCw size={16} class={loading ? 'animate-spin' : ''} />
			Aktualisieren
		</Button>
	{/snippet}
</PageHeader>

{#if error}
	<Alert variant="error" class="mb-6">{error}</Alert>
{/if}

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
								<span
									class="font-mono text-sm {isMaster
										? 'text-royal-300 font-semibold'
										: 'text-royal-300'}"
								>
									{product.sku}
								</span>
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
									{@const url = actindoProductUrl(product.productId)}
									<div class="flex items-center gap-1.5">
										<span class="font-mono text-sm">{product.productId}</span>
										{#if url}
											<a
												href={url}
												target="_blank"
												rel="noopener noreferrer"
												onclick={(e) => e.stopPropagation()}
												class="text-gray-600 hover:text-royal-400 transition-colors"
												title="In Actindo öffnen"
											>
												<ExternalLink size={12} />
											</a>
										{/if}
									</div>
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
										<span class="font-mono text-sm text-gray-400 pl-4 whitespace-nowrap">
											<span class="text-royal-600 mr-1">└</span>
											{variant.sku}
										</span>
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
											{@const varUrl = actindoProductUrl(variant.productId)}
											<div class="flex items-center gap-1.5">
												<span class="font-mono text-sm">{variant.productId}</span>
												{#if varUrl}
													<a
														href={varUrl}
														target="_blank"
														rel="noopener noreferrer"
														onclick={(e) => e.stopPropagation()}
														class="text-gray-600 hover:text-royal-400 transition-colors"
														title="In Actindo öffnen"
													>
														<ExternalLink size={12} />
													</a>
												{/if}
											</div>
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
