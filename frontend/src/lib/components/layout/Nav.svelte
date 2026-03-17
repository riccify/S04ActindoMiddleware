<script lang="ts">
	import { page } from '$app/stores';
	import { LogOut, LayoutDashboard, Package, Users, Settings, UserCircle, ArrowRightLeft, ChevronDown, Key, Zap } from 'lucide-svelte';
	import { authStore, permissions, currentUser } from '$stores/auth';
	import { dashboardStore } from '$stores/dashboard';
	import StatusPill from '$components/ui/StatusPill.svelte';
	import Button from '$components/ui/Button.svelte';
	import Modal from '$components/ui/Modal.svelte';

	let summary = $derived($dashboardStore.summary);
	let perms = $derived($permissions);
	let user = $derived($currentUser);

	let dropdownOpen = $state(false);
	let passwordModalOpen = $state(false);
	let currentPassword = $state('');
	let newPassword = $state('');
	let confirmPassword = $state('');
	let passwordError = $state('');
	let passwordSuccess = $state(false);
	let passwordLoading = $state(false);

	interface NavLink {
		href: string;
		label: string;
		icon: typeof LayoutDashboard;
		adminOnly?: boolean;
		writeOnly?: boolean;
	}

	const links: NavLink[] = [
		{ href: '/', label: 'Dashboard', icon: LayoutDashboard },
		{ href: '/products', label: 'Products', icon: Package },
		{ href: '/customers', label: 'Customers', icon: Users },
		{ href: '/jobs', label: 'Jobs', icon: Zap },
		{ href: '/sync', label: 'Sync', icon: ArrowRightLeft, writeOnly: true },
		{ href: '/users', label: 'Users', icon: UserCircle, adminOnly: true },
		{ href: '/settings', label: 'Settings', icon: Settings, adminOnly: true }
	];

	function isActive(href: string): boolean {
		if (href === '/') return $page.url.pathname === '/';
		return $page.url.pathname.startsWith(href);
	}

	async function handleLogout() {
		await authStore.logout();
		window.location.href = '/login';
	}

	function toggleDropdown() {
		dropdownOpen = !dropdownOpen;
	}

	function closeDropdown() {
		dropdownOpen = false;
	}

	function openPasswordModal() {
		dropdownOpen = false;
		passwordModalOpen = true;
		currentPassword = '';
		newPassword = '';
		confirmPassword = '';
		passwordError = '';
		passwordSuccess = false;
	}

	async function handleChangePassword() {
		passwordError = '';
		passwordSuccess = false;

		if (!currentPassword || !newPassword || !confirmPassword) {
			passwordError = 'Bitte alle Felder ausfuellen';
			return;
		}

		if (newPassword !== confirmPassword) {
			passwordError = 'Passwoerter stimmen nicht ueberein';
			return;
		}

		if (newPassword.length < 4) {
			passwordError = 'Neues Passwort muss mindestens 4 Zeichen lang sein';
			return;
		}

		passwordLoading = true;
		try {
			await authStore.changePassword(currentPassword, newPassword);
			passwordSuccess = true;
			currentPassword = '';
			newPassword = '';
			confirmPassword = '';
			setTimeout(() => {
				passwordModalOpen = false;
				passwordSuccess = false;
			}, 1500);
		} catch (err: unknown) {
			const errorMessage = err instanceof Error ? err.message : 'Unbekannter Fehler';
			if (errorMessage.includes('incorrect')) {
				passwordError = 'Aktuelles Passwort ist falsch';
			} else {
				passwordError = errorMessage;
			}
		} finally {
			passwordLoading = false;
		}
	}

	function handleClickOutside(event: MouseEvent) {
		const target = event.target as HTMLElement;
		if (!target.closest('.user-dropdown')) {
			closeDropdown();
		}
	}
</script>

<svelte:window onclick={handleClickOutside} />

<nav class="sticky top-0 z-40 glass border-b border-white/5">
	<div class="max-w-7xl mx-auto px-4 sm:px-6">
		<div class="flex items-center justify-between h-16 gap-4">
			<!-- Brand -->
			<div class="flex-shrink-0 flex items-center gap-2.5">
				<span class="text-lg font-bold tracking-wider uppercase text-white">
					Actindo <span class="text-royal-400">Middleware</span>
				</span>
				{#if summary?.environment === 'Development'}
					<span class="text-xs font-semibold px-2 py-0.5 rounded-full bg-yellow-500/20 border border-yellow-500/40 text-yellow-300 uppercase tracking-wide">
						Dev
					</span>
				{:else if summary?.environment === 'Production'}
					<span class="text-xs font-semibold px-2 py-0.5 rounded-full bg-green-500/20 border border-green-500/40 text-green-300 uppercase tracking-wide">
						Live
					</span>
				{/if}
			</div>

			<!-- Navigation Links -->
			<div class="hidden md:flex items-center gap-1">
				{#each links as link}
					{#if (!link.adminOnly || perms.isAdmin) && (!link.writeOnly || perms.canWrite)}
						<a
							href={link.href}
							class="
								flex items-center gap-2 px-4 py-2 rounded-full text-sm font-medium
								uppercase tracking-wide transition-all
								{isActive(link.href)
									? 'bg-royal-600/30 text-white border border-royal-600/50'
									: 'text-gray-400 hover:text-white hover:bg-white/5'}
							"
						>
							<svelte:component this={link.icon} size={16} />
							{link.label}
						</a>
					{/if}
				{/each}
			</div>

			<!-- User Dropdown -->
			{#if user}
				<div class="relative user-dropdown">
					<button
						type="button"
						onclick={toggleDropdown}
						class="
							flex items-center gap-2 px-3 py-1.5 rounded-full
							bg-gradient-to-r from-royal-400/20 to-royal-600/20
							border border-royal-400/30
							hover:from-royal-400/30 hover:to-royal-600/30
							transition-all cursor-pointer
						"
					>
						<span class="w-2 h-2 rounded-full bg-royal-400 animate-pulse"></span>
						<span class="text-sm font-medium">{user.username}</span>
						<ChevronDown size={14} class="transition-transform {dropdownOpen ? 'rotate-180' : ''}" />
					</button>

					{#if dropdownOpen}
						<div
							class="
								absolute right-0 mt-2 w-64
								bg-dark-800 border border-white/10 rounded-xl
								shadow-2xl overflow-hidden z-50
							"
						>
							<!-- Status Pills -->
							<div class="p-3 border-b border-white/10">
								<div class="text-xs text-gray-500 uppercase tracking-wide mb-2">Status</div>
								<div class="flex flex-col gap-2">
									<StatusPill
										state={summary?.actindo?.state ?? 'unknown'}
										label="Actindo"
										tooltip={summary?.actindo?.message ?? 'Status wird geladen...'}
									/>
									<StatusPill
										state={summary?.oauth?.state ?? 'unknown'}
										label="Token"
										tooltip={summary?.oauth?.message ?? 'Status wird geladen...'}
									/>
								</div>
							</div>

							<!-- Actions -->
							<div class="p-2">
								<button
									type="button"
									onclick={openPasswordModal}
									class="
										w-full flex items-center gap-3 px-3 py-2 rounded-lg
										text-sm text-gray-300 hover:text-white hover:bg-white/5
										transition-colors text-left
									"
								>
									<Key size={16} />
									Passwort aendern
								</button>
								<button
									type="button"
									onclick={handleLogout}
									class="
										w-full flex items-center gap-3 px-3 py-2 rounded-lg
										text-sm text-red-400 hover:text-red-300 hover:bg-red-500/10
										transition-colors text-left
									"
								>
									<LogOut size={16} />
									Logout
								</button>
							</div>
						</div>
					{/if}
				</div>
			{/if}
		</div>
	</div>

	<!-- Mobile Navigation -->
	<div class="md:hidden border-t border-white/5">
		<div class="flex overflow-x-auto px-4 py-2 gap-2">
			{#each links as link}
				{#if (!link.adminOnly || perms.isAdmin) && (!link.writeOnly || perms.canWrite)}
					<a
						href={link.href}
						class="
							flex items-center gap-2 px-3 py-1.5 rounded-full text-xs font-medium
							uppercase tracking-wide whitespace-nowrap transition-all
							{isActive(link.href)
								? 'bg-royal-600/30 text-white border border-royal-600/50'
								: 'text-gray-400 hover:text-white hover:bg-white/5'}
						"
					>
						<svelte:component this={link.icon} size={14} />
						{link.label}
					</a>
				{/if}
			{/each}
		</div>
	</div>
</nav>

<!-- Password Change Modal -->
<Modal bind:open={passwordModalOpen} title="Passwort aendern">
	<div class="space-y-4">
		{#if passwordSuccess}
			<div class="p-4 bg-green-500/20 border border-green-500/30 rounded-lg text-green-400 text-center">
				Passwort erfolgreich geaendert!
			</div>
		{:else}
			{#if passwordError}
				<div class="p-3 bg-red-500/20 border border-red-500/30 rounded-lg text-red-400 text-sm">
					{passwordError}
				</div>
			{/if}

			<div>
				<label for="currentPassword" class="block text-sm font-medium text-gray-400 mb-1">
					Aktuelles Passwort
				</label>
				<input
					type="password"
					id="currentPassword"
					bind:value={currentPassword}
					class="
						w-full px-4 py-2 rounded-lg
						bg-dark-700 border border-white/10
						text-white placeholder-gray-500
						focus:outline-none focus:border-royal-500
					"
					placeholder="Aktuelles Passwort eingeben"
				/>
			</div>

			<div>
				<label for="newPassword" class="block text-sm font-medium text-gray-400 mb-1">
					Neues Passwort
				</label>
				<input
					type="password"
					id="newPassword"
					bind:value={newPassword}
					class="
						w-full px-4 py-2 rounded-lg
						bg-dark-700 border border-white/10
						text-white placeholder-gray-500
						focus:outline-none focus:border-royal-500
					"
					placeholder="Neues Passwort eingeben"
				/>
			</div>

			<div>
				<label for="confirmPassword" class="block text-sm font-medium text-gray-400 mb-1">
					Passwort wiederholen
				</label>
				<input
					type="password"
					id="confirmPassword"
					bind:value={confirmPassword}
					class="
						w-full px-4 py-2 rounded-lg
						bg-dark-700 border border-white/10
						text-white placeholder-gray-500
						focus:outline-none focus:border-royal-500
					"
					placeholder="Neues Passwort wiederholen"
				/>
			</div>

			<div class="flex justify-end gap-3 pt-2">
				<Button variant="ghost" onclick={() => (passwordModalOpen = false)}>
					Abbrechen
				</Button>
				<Button onclick={handleChangePassword} disabled={passwordLoading}>
					{passwordLoading ? 'Speichern...' : 'Passwort aendern'}
				</Button>
			</div>
		{/if}
	</div>
</Modal>
