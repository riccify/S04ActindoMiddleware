import type {
	User,
	LoginRequest,
	RegisterRequest,
	BootstrapRequest,
	DashboardSummary,
	ProductListItem,
	ProductStockItem,
	ProductJobInfo,
	ProductJobLogEntry,
	CustomerListItem,
	UserDto,
	CreateUserRequest,
	SetRoleRequest,
	Registration,
	ApproveRegistrationRequest,
	ActindoSettings,
	ProductSyncStatus,
	CustomerSyncStatus
} from './types';

class ApiError extends Error {
	constructor(
		public status: number,
		message: string
	) {
		super(message);
		this.name = 'ApiError';
	}
}

async function request<T>(
	url: string,
	options: RequestInit = {}
): Promise<T> {
	console.log(`[API] ${options.method || 'GET'} ${url}`, options.body ? JSON.parse(options.body as string) : '');

	const response = await fetch(url, {
		...options,
		headers: {
			'Content-Type': 'application/json',
			...options.headers
		}
	});

	console.log(`[API] ${options.method || 'GET'} ${url} => ${response.status}`);

	if (response.status === 401) {
		console.warn('[API] 401 Unauthorized - redirecting to login');
		if (typeof window !== 'undefined' && !window.location.pathname.includes('/login')) {
			window.location.href = `/login?returnUrl=${encodeURIComponent(window.location.pathname)}`;
		}
		throw new ApiError(401, 'Nicht authentifiziert');
	}

	if (response.status === 403) {
		console.warn('[API] 403 Forbidden');
		throw new ApiError(403, 'Keine Berechtigung');
	}

	if (!response.ok) {
		const text = await response.text();
		console.error(`[API] Error ${response.status}: ${text}`);
		throw new ApiError(response.status, text || `Request failed: ${response.status}`);
	}

	if (response.status === 204) {
		console.log('[API] 204 No Content');
		return undefined as T;
	}

	const json = await response.json();
	console.log(`[API] Response:`, json);
	return json;
}

// Auth API
export const auth = {
	me: () => request<User>('/api/auth/me'),

	bootstrapNeeded: () => request<{ needed: boolean }>('/api/auth/bootstrap-needed'),

	bootstrap: (data: BootstrapRequest) =>
		request<{ username: string; role: string }>('/api/auth/bootstrap', {
			method: 'POST',
			body: JSON.stringify(data)
		}),

	login: (data: LoginRequest) =>
		request<{ username: string; role: string }>('/api/auth/login', {
			method: 'POST',
			body: JSON.stringify(data)
		}),

	register: (data: RegisterRequest) =>
		request<{ id: string; username: string; createdAt: string }>('/api/auth/register', {
			method: 'POST',
			body: JSON.stringify(data)
		}),

	logout: () =>
		request<void>('/api/auth/logout', {
			method: 'POST'
		}),

	changePassword: (data: { currentPassword: string; newPassword: string }) =>
		request<void>('/api/auth/change-password', {
			method: 'POST',
			body: JSON.stringify(data)
		})
};

// Dashboard API
export const dashboard = {
	summary: () => request<DashboardSummary>('/api/dashboard/summary')
};

// Products API
export const products = {
	list: (params: { limit?: number; includeVariants?: boolean } = {}) => {
		const searchParams = new URLSearchParams();
		if (params.limit) searchParams.set('limit', params.limit.toString());
		if (params.includeVariants) searchParams.set('includeVariants', 'true');
		return request<ProductListItem[]>(`/api/products?${searchParams}`);
	},

	sync: (includeVariants = false) => {
		const searchParams = new URLSearchParams();
		if (includeVariants) searchParams.set('includeVariants', 'true');
		return request<ProductListItem[]>(`/api/products/sync?${searchParams}`);
	},

	getVariants: (masterSku: string) =>
		request<ProductListItem[]>(`/api/products/${encodeURIComponent(masterSku)}/variants`),

	getStocks: (sku: string) =>
		request<ProductStockItem[]>(`/api/products/${encodeURIComponent(sku)}/stocks`),

	delete: (data: { productId: number; jobId: string; sku: string }) =>
		request<void>('/api/products/delete', {
			method: 'POST',
			body: JSON.stringify(data)
		}),

	activeJobs: () => request<ProductJobInfo[]>('/api/actindo/products/active-jobs'),

	jobLogs: (jobId: string) =>
		request<ProductJobLogEntry[]>(`/api/actindo/products/active-jobs/${jobId}/logs`),

	logReplay: (endpoint: string, requestPayload: string) =>
		request<{ success: boolean; responsePayload: string | null; error?: string }>(
			'/api/actindo/products/log-replay',
			{ method: 'POST', body: JSON.stringify({ endpoint, requestPayload }) }
		)
};

// Customers API
export const customers = {
	list: (limit = 200) => request<CustomerListItem[]>(`/api/customers?limit=${limit}`)
};

// Users API
export const users = {
	list: () => request<UserDto[]>('/api/users'),

	create: (data: CreateUserRequest) =>
		request<UserDto>('/api/users', {
			method: 'POST',
			body: JSON.stringify(data)
		}),

	setRole: (userId: string, data: SetRoleRequest) =>
		request<void>(`/api/users/${userId}/role`, {
			method: 'PUT',
			body: JSON.stringify(data)
		}),

	delete: (userId: string) =>
		request<void>(`/api/users/${userId}`, { method: 'DELETE' })
};

// Registrations API
export const registrations = {
	list: () => request<Registration[]>('/api/registrations'),

	approve: (registrationId: string, data: ApproveRegistrationRequest) =>
		request<void>(`/api/registrations/${registrationId}/approve`, {
			method: 'POST',
			body: JSON.stringify(data)
		}),

	reject: (registrationId: string) =>
		request<void>(`/api/registrations/${registrationId}`, { method: 'DELETE' })
};

// Settings API
export const settings = {
	get: () => request<ActindoSettings>('/api/settings/actindo'),

	update: (data: ActindoSettings) =>
		request<void>('/api/settings/actindo', {
			method: 'PUT',
			body: JSON.stringify(data)
		}),

	resetTokens: () =>
		request<void>('/api/actindo/auth/reset', { method: 'POST' })
};

// Sync API
export const sync = {
	status: () => request<{ configured: boolean }>('/api/sync/status'),

	getProductStatus: () => request<ProductSyncStatus>('/api/sync/products'),

	getCustomerStatus: () => request<CustomerSyncStatus>('/api/sync/customers'),

	syncProducts: (skus: string[]) =>
		request<{ synced: number }>('/api/sync/products', {
			method: 'POST',
			body: JSON.stringify({ skus })
		}),

	syncAllProducts: () =>
		request<{ synced: number; message?: string }>('/api/sync/products/all', {
			method: 'POST'
		}),

	syncCustomer: (debtorNumber: string) =>
		request<{ synced: number }>('/api/sync/customers', {
			method: 'POST',
			body: JSON.stringify({ debtorNumber })
		}),

	syncAllCustomers: () =>
		request<{ synced: number; errors?: string[] }>('/api/sync/customers/all', {
			method: 'POST'
		}),

	clearProductIds: (skus: string[]) =>
		request<{ cleared: number; message?: string }>('/api/sync/products/clear', {
			method: 'POST',
			body: JSON.stringify({ skus })
		}),

	forceSyncProducts: (skus: string[]) =>
		request<{ synced: number; message?: string }>('/api/sync/products/force', {
			method: 'POST',
			body: JSON.stringify({ skus })
		})
};

export { ApiError };
