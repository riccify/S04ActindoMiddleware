<script lang="ts">
	import type { Snippet } from 'svelte';
	import { X } from 'lucide-svelte';

	interface Props {
		open: boolean;
		title?: string;
		class?: string;
		onclose?: () => void;
		children: Snippet;
		footer?: Snippet;
		headerActions?: Snippet;
	}

	let {
		open = $bindable(false),
		title = '',
		class: className = '',
		onclose,
		children,
		footer,
		headerActions
	}: Props = $props();

	function handleBackdropClick(e: MouseEvent) {
		if (e.target === e.currentTarget) {
			open = false;
			onclose?.();
		}
	}

	function handleKeydown(e: KeyboardEvent) {
		if (e.key === 'Escape') {
			open = false;
			onclose?.();
		}
	}
</script>

<svelte:window onkeydown={handleKeydown} />

{#if open}
	<!-- svelte-ignore a11y_click_events_have_key_events a11y_interactive_supports_focus -->
	<div
		class="
			fixed inset-0 z-50
			flex items-center justify-center p-4
			bg-dark-900/85 backdrop-blur-sm
			animate-fade-in
		"
		onclick={handleBackdropClick}
		role="dialog"
		aria-modal="true"
	>
		<div
			class="
				w-full max-w-2xl
				bg-dark-800 border border-white/10 rounded-2xl
				shadow-2xl animate-slide-up
				{className}
			"
		>
			{#if title}
				<div class="flex items-center justify-between px-6 py-4 border-b border-white/10">
					<h2 class="text-lg font-semibold">{title}</h2>
					<div class="flex items-center gap-2">
						{#if headerActions}
							{@render headerActions()}
						{/if}
						<button
							type="button"
							class="p-2 rounded-lg hover:bg-white/10 transition-colors"
							onclick={() => {
								open = false;
								onclose?.();
							}}
						>
							<X size={20} />
						</button>
					</div>
				</div>
			{/if}

			<div class="p-6">
				{@render children()}
			</div>

			{#if footer}
				<div class="flex justify-end gap-3 px-6 py-4 border-t border-white/10">
					{@render footer()}
				</div>
			{/if}
		</div>
	</div>
{/if}
