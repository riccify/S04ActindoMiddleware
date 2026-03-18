<script lang="ts">
	import { onMount } from 'svelte';
	import { goto } from '$app/navigation';
	import { Save, RefreshCw, Trash2, Settings as SettingsIcon, Key, Link, Globe, Warehouse, Plus, X } from 'lucide-svelte';
	import { settings as settingsApi } from '$api/client';
	import type { ActindoSettings } from '$api/types';
	import { permissions } from '$stores/auth';
	import { formatDate } from '$utils/format';
	import PageHeader from '$components/layout/PageHeader.svelte';
	import Card from '$components/ui/Card.svelte';
	import Button from '$components/ui/Button.svelte';
	import Input from '$components/ui/Input.svelte';
	import Alert from '$components/ui/Alert.svelte';
	import Spinner from '$components/ui/Spinner.svelte';
	import Badge from '$components/ui/Badge.svelte';

	let perms = $derived($permissions);

	let settings: ActindoSettings | null = $state(null);
	let loading = $state(true);
	let saving = $state(false);
	let error = $state('');
	let success = $state('');

	// Form fields
	let clientId = $state('');
	let clientSecret = $state('');
	let tokenEndpoint = $state('');
	let accessToken = $state('');
	let refreshToken = $state('');
	let endpoints: Record<string, string> = $state({});
	let navApiUrl = $state('');
	let navApiToken = $state('');
	let actindoBaseUrl = $state('');
	let warehouseMappings: Record<string, number> = $state({});
	let newWarehouseName = $state('');
	let newWarehouseIdInput = $state('');

	onMount(() => {
		if (!perms.isAdmin) {
			goto('/');
			return;
		}
		loadSettings();
	});

	async function loadSettings() {
		loading = true;
		error = '';
		try {
			const data = await settingsApi.get();
			settings = data;
			clientId = data.clientId ?? '';
			clientSecret = data.clientSecret ?? '';
			tokenEndpoint = data.tokenEndpoint ?? '';
			accessToken = data.accessToken ?? '';
			refreshToken = data.refreshToken ?? '';
			endpoints = { ...data.endpoints };
			navApiUrl = data.navApiUrl ?? '';
			navApiToken = data.navApiToken ?? '';
			actindoBaseUrl = data.actindoBaseUrl ?? 'https://schalke-dev.dev.actindo.com';
			warehouseMappings = { ...data.warehouseMappings };
		} catch (err) {
			error = err instanceof Error ? err.message : 'Fehler beim Laden';
		} finally {
			loading = false;
		}
	}

	async function handleSave() {
		saving = true;
		error = '';
		success = '';

		try {
			await settingsApi.update({
				clientId: clientId || null,
				clientSecret: clientSecret || null,
				tokenEndpoint: tokenEndpoint || null,
				accessToken: accessToken || null,
				accessTokenExpiresAt: settings?.accessTokenExpiresAt ?? null,
				refreshToken: refreshToken || null,
				endpoints,
				navApiUrl: navApiUrl || null,
				navApiToken: navApiToken || null,
				actindoBaseUrl: actindoBaseUrl || 'https://schalke-dev.dev.actindo.com',
				warehouseMappings
			});
			success = 'Einstellungen gespeichert';
			loadSettings();
		} catch (err) {
			error = err instanceof Error ? err.message : 'Fehler beim Speichern';
		} finally {
			saving = false;
		}
	}

	async function handleResetTokens() {
		if (!confirm('OAuth-Tokens wirklich zuruecksetzen?')) return;
		try {
			await settingsApi.resetTokens();
			accessToken = '';
			refreshToken = '';
			success = 'Tokens zurueckgesetzt';
			loadSettings();
		} catch (err) {
			error = err instanceof Error ? err.message : 'Fehler beim Zuruecksetzen';
		}
	}

	function updateEndpoint(key: string, value: string) {
		endpoints = { ...endpoints, [key]: value };
	}

	function addWarehouseMapping() {
		const name = newWarehouseName.trim();
		const id = parseInt(newWarehouseIdInput.trim(), 10);
		if (!name || isNaN(id)) return;
		warehouseMappings = { ...warehouseMappings, [name]: id };
		newWarehouseName = '';
		newWarehouseIdInput = '';
	}

	function removeWarehouseMapping(name: string) {
		const { [name]: _, ...rest } = warehouseMappings;
		warehouseMappings = rest;
	}
</script>

<svelte:head>
	<title>Settings | Actindo Middleware</title>
</svelte:head>

<PageHeader title="Einstellungen" subtitle="Actindo OAuth und API-Konfiguration">
	{#snippet actions()}
		<Button variant="ghost" onclick={loadSettings} disabled={loading}>
			<RefreshCw size={16} class={loading ? 'animate-spin' : ''} />
			Aktualisieren
		</Button>
	{/snippet}
</PageHeader>

{#if error}
	<Alert variant="error" class="mb-6" dismissible ondismiss={() => (error = '')}>{error}</Alert>
{/if}

{#if success}
	<Alert variant="success" class="mb-6" dismissible ondismiss={() => (success = '')}>{success}</Alert>
{/if}

{#if loading && !settings}
	<div class="flex justify-center py-12">
		<Spinner size="large" />
	</div>
{:else}
	<div class="grid lg:grid-cols-2 gap-6">
		<!-- OAuth Credentials -->
		<Card>
			<h3 class="text-lg font-semibold mb-4 flex items-center gap-2">
				<Key size={20} />
				OAuth Credentials
			</h3>

			<div class="space-y-4">
				<div>
					<label class="label" for="token-endpoint">Token Endpoint</label>
					<Input
						id="token-endpoint"
						bind:value={tokenEndpoint}
						placeholder="https://..."
					/>
				</div>
				<div>
					<label class="label" for="client-id">Client ID</label>
					<Input id="client-id" bind:value={clientId} placeholder="Client ID" />
				</div>
				<div>
					<label class="label" for="client-secret">Client Secret</label>
					<Input
						id="client-secret"
						type="password"
						bind:value={clientSecret}
						placeholder="********"
					/>
				</div>
			</div>
		</Card>

		<!-- Token Status -->
		<Card>
			<div class="flex items-center justify-between mb-4">
				<h3 class="text-lg font-semibold flex items-center gap-2">
					<SettingsIcon size={20} />
					Token Status
				</h3>
				<Button variant="danger" size="small" onclick={handleResetTokens}>
					<Trash2 size={14} />
					Tokens loeschen
				</Button>
			</div>

			<div class="space-y-4">
				<div class="p-4 rounded-xl bg-black/20 border border-white/10">
					<div class="flex items-center justify-between mb-2">
						<span class="text-sm text-gray-400">Access Token</span>
						{#if accessToken}
							<Badge variant="success">Vorhanden</Badge>
						{:else}
							<Badge variant="error">Fehlt</Badge>
						{/if}
					</div>
					{#if settings?.accessTokenExpiresAt}
						<p class="text-sm text-gray-500">
							Gueltig bis: {formatDate(settings.accessTokenExpiresAt)}
						</p>
					{/if}
				</div>

				<div class="p-4 rounded-xl bg-black/20 border border-white/10">
					<div class="flex items-center justify-between">
						<span class="text-sm text-gray-400">Refresh Token</span>
						{#if refreshToken}
							<Badge variant="success">Vorhanden</Badge>
						{:else}
							<Badge variant="warning">Fehlt</Badge>
						{/if}
					</div>
				</div>

				<div>
					<label class="label" for="access-token">Access Token (manuell)</label>
					<Input
						id="access-token"
						type="password"
						bind:value={accessToken}
						placeholder="Access Token..."
					/>
				</div>
				<div>
					<label class="label" for="refresh-token">Refresh Token (manuell)</label>
					<Input
						id="refresh-token"
						type="password"
						bind:value={refreshToken}
						placeholder="Refresh Token..."
					/>
				</div>
			</div>
		</Card>

		<!-- Actindo Base URL -->
		<Card>
			<h3 class="text-lg font-semibold mb-4 flex items-center gap-2">
				<Link size={20} />
				Actindo Base URL
			</h3>
			<div>
				<label class="label" for="actindo-base-url">Actindo Frontend URL</label>
				<Input
					id="actindo-base-url"
					bind:value={actindoBaseUrl}
					placeholder="https://schalke-dev.dev.actindo.com"
				/>
				<p class="text-xs text-gray-500 mt-1">Wird für direkte Links zu Produkten in Actindo verwendet.</p>
			</div>
		</Card>

		<!-- NAV API Settings -->
		<Card class="lg:col-span-2">
			<div class="flex items-center justify-between mb-4">
				<h3 class="text-lg font-semibold flex items-center gap-2">
					<Globe size={20} />
					NAV API Einstellungen
				</h3>
				{#if navApiUrl && navApiToken}
					<Badge variant="success">Konfiguriert</Badge>
				{:else}
					<Badge variant="warning">Nicht konfiguriert</Badge>
				{/if}
			</div>

			<div class="grid sm:grid-cols-2 gap-4">
				<div>
					<label class="label" for="nav-api-url">NAV API URL</label>
					<Input
						id="nav-api-url"
						bind:value={navApiUrl}
						placeholder="https://notify.schalke04.de/nav/test/navapi"
					/>
					{#if navApiUrl}
						<p class="text-xs text-green-500 mt-1">URL gesetzt</p>
					{:else}
						<p class="text-xs text-gray-500 mt-1">Endpoint fuer NAV API Aufrufe</p>
					{/if}
				</div>
				<div>
					<label class="label" for="nav-api-token">NAV API Token</label>
					<Input
						id="nav-api-token"
						type="password"
						bind:value={navApiToken}
						placeholder="Bearer Token..."
					/>
					{#if navApiToken}
						<p class="text-xs text-green-500 mt-1">Token gesetzt ({navApiToken.length} Zeichen)</p>
					{:else}
						<p class="text-xs text-gray-500 mt-1">Bearer Token fuer die Authentifizierung</p>
					{/if}
				</div>
			</div>
		</Card>

		<!-- Actindo Endpoints -->
		<Card class="lg:col-span-2">
			<h3 class="text-lg font-semibold mb-4 flex items-center gap-2">
				<Link size={20} />
				Actindo Endpoints
			</h3>

			<div class="grid sm:grid-cols-2 gap-4">
				{#each Object.entries(endpoints) as [key, value]}
					<div>
						<label class="label" for="endpoint-{key}">{key}</label>
						<Input
							id="endpoint-{key}"
							{value}
							oninput={(e) => updateEndpoint(key, e.currentTarget.value)}
							placeholder="https://..."
						/>
					</div>
				{/each}
			</div>
		</Card>

		<!-- Lager-Konfiguration -->
	<Card class="lg:col-span-2">
		<h3 class="text-lg font-semibold mb-4 flex items-center gap-2">
			<Warehouse size={20} />
			Lager-Konfiguration
		</h3>
		<p class="text-sm text-gray-400 mb-4">Lager-Namen auf Actindo Warehouse-IDs mappen. Wird fuer Bestandsupdates benoetigt.</p>

		{#if Object.keys(warehouseMappings).length > 0}
			<table class="w-full mb-4">
				<thead>
					<tr class="border-b border-white/10">
						<th class="text-left py-2 text-xs uppercase tracking-wider text-gray-400 font-medium">Lager-Name</th>
						<th class="text-left py-2 text-xs uppercase tracking-wider text-gray-400 font-medium">Actindo ID</th>
						<th class="w-10"></th>
					</tr>
				</thead>
				<tbody>
					{#each Object.entries(warehouseMappings) as [name, id]}
						<tr class="border-b border-white/5">
							<td class="py-2 font-mono text-sm">{name}</td>
							<td class="py-2 font-mono text-sm text-royal-300">{id}</td>
							<td class="py-2">
								<button type="button" onclick={() => removeWarehouseMapping(name)} class="p-1 rounded hover:bg-white/10 text-gray-400 hover:text-red-400 transition-colors">
									<X size={14} />
								</button>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		{:else}
			<p class="text-sm text-gray-500 mb-4">Noch keine Mappings konfiguriert.</p>
		{/if}

		<div class="flex gap-2 items-end">
			<div class="flex-1">
				<label class="label" for="new-wh-name">Lager-Name</label>
				<Input id="new-wh-name" bind:value={newWarehouseName} placeholder="z.B. GE-City" />
			</div>
			<div class="w-36">
				<label class="label" for="new-wh-id">Actindo ID</label>
				<Input id="new-wh-id" bind:value={newWarehouseIdInput} placeholder="z.B. 20" />
			</div>
			<Button variant="ghost" onclick={addWarehouseMapping}>
				<Plus size={16} />
				Hinzufuegen
			</Button>
		</div>
		</Card>
	</div>

	<!-- Save Button -->
	<div class="flex justify-end mt-6">
		<Button onclick={handleSave} disabled={saving}>
			<Save size={16} />
			{saving ? 'Speichere...' : 'Einstellungen speichern'}
		</Button>
	</div>
{/if}
