// Active Product Sync Jobs
export type ProductSyncJobStatus = 'queued' | 'running' | 'completed' | 'failed';

export interface ProductJobLogEntry {
	timestamp: string;
	endpoint: string;
	success: boolean;
	error: string | null;
	requestPayload: string | null;
	responsePayload: string | null;
}

export interface ProductJobInfo {
	id: string;
	sku: string;
	operation: string; // "create" | "save" | "full"
	bufferId: string | null;
	status: ProductSyncJobStatus;
	queuedAt: string;
	startedAt: string | null;
	completedAt: string | null;
	error: string | null;
	navRequestPayload: string | null;
	navResponsePayload: string | null;
}

// Auth Types
export interface User {
	authenticated: boolean;
	username: string;
	role: 'read' | 'write' | 'admin';
}

export interface LoginRequest {
	username: string;
	password: string;
}

export interface RegisterRequest {
	username: string;
	password: string;
}

export interface BootstrapRequest {
	username: string;
	password: string;
}

// Dashboard Types
export interface MetricStats {
	title: string;
	success: number;
	failed: number;
	averageDurationSeconds: number;
}

export interface OAuthStatus {
	state: 'ok' | 'warning' | 'error' | 'unknown';
	message: string;
	expiresAt: string | null;
	hasRefreshToken: boolean;
}

export interface ActindoStatus {
	state: 'ok' | 'warning' | 'error' | 'unknown';
	message: string;
	lastSuccessAt: string | null;
	lastFailureAt: string | null;
}

export interface DashboardSummary {
	generatedAt: string;
	environment: string;
	activeJobs: number;
	products: MetricStats;
	customers: MetricStats;
	transactions: MetricStats;
	media: MetricStats;
	oauth: OAuthStatus;
	actindo: ActindoStatus;
}

// Product Types
export type VariantStatus = 'single' | 'master' | 'child';

export interface ProductListItem {
	jobId: string;
	productId: number | null;
	sku: string;
	name: string;
	variantCount: number | null;
	createdAt: string | null;
	variantStatus: VariantStatus;
	parentSku: string | null;
	variantCode: string | null;
	// Preis- und Bestandsdaten
	lastPrice: number | null;
	lastPriceEmployee: number | null;
	lastPriceMember: number | null;
	lastStock: number | null;
	lastWarehouseId: number | null;
	lastPriceUpdatedAt: string | null;
	lastStockUpdatedAt: string | null;
}

// Product Stock Types
export interface ProductStockItem {
	sku: string;
	warehouseId: number;
	stock: number;
	updatedAt: string;
}

// Customer Types
export interface CustomerListItem {
	jobId: string;
	customerId: number | null;
	debtorNumber: string;
	name: string;
	createdAt: string | null;
}

// User Management Types
export interface UserDto {
	id: string;
	username: string;
	role: string;
	createdAt: string;
}

export interface CreateUserRequest {
	username: string;
	password: string;
	role: 'read' | 'write' | 'admin';
}

export interface SetRoleRequest {
	role: 'read' | 'write' | 'admin';
}

// Registration Types
export interface Registration {
	id: string;
	username: string;
	createdAt: string;
}

export interface ApproveRegistrationRequest {
	role: 'read' | 'write' | 'admin';
}

// Settings Types
export interface ActindoSettings {
	accessToken: string | null;
	accessTokenExpiresAt: string | null;
	refreshToken: string | null;
	tokenEndpoint: string | null;
	clientId: string | null;
	clientSecret: string | null;
	endpoints: Record<string, string>;
	navApiUrl: string | null;
	navApiToken: string | null;
	warehouseMappings: Record<string, number>;
	actindoBaseUrl: string | null;
}

export interface TokenValidationResult {
	valid: boolean;
	message: string;
}

export interface ActindoTokenValidationResponse {
	accessToken: TokenValidationResult;
	refreshToken: TokenValidationResult;
}

export interface NavApiValidationResponse {
	navApi: TokenValidationResult;
}

// NAV Sync Error Types
export interface NavSyncMissingVariant {
	sku: string;
	actindoId: number;
	status: 'missing' | 'mismatch';
	navActindoId: string | null;
}

export interface NavSyncMissingItem {
	sku: string;
	actindoId: number;
	variantStatus: 'single' | 'master';
	status: 'missing' | 'mismatch' | 'ok';
	navActindoId: string | null;
	totalVariants: number;
	missingVariants: NavSyncMissingVariant[];
}

export interface NavSyncErrorsDto {
	totalInActindo: number;
	missingFromNav: number;
	items: NavSyncMissingItem[];
}

// Sync Types
export type SyncStatus = 'Synced' | 'NeedsSync' | 'Mismatch' | 'Orphan' | 'ActindoOnly' | 'NavOnly';

export interface ProductVariantSyncItem {
	sku: string;
	variantCode: string;
	name: string;
	actindoId: string | null;
	navActindoId: string | null;
	middlewareActindoId: string | null;
	inActindo: boolean;
	inNav: boolean;
	inMiddleware: boolean;
	status: SyncStatus;
}

export interface ProductSyncItem {
	sku: string;
	name: string;
	variantStatus: VariantStatus;
	actindoId: string | null;
	navActindoId: string | null;
	middlewareActindoId: string | null;
	inActindo: boolean;
	inNav: boolean;
	inMiddleware: boolean;
	status: SyncStatus;
	needsSync: boolean;
	isOrphan: boolean;
	isMismatch: boolean;
	variants: ProductVariantSyncItem[];
}

export interface CustomerSyncItem {
	debtorNumber: string;
	name: string;
	middlewareActindoId: number | null;
	navNavId: number | null;
	navActindoId: number | null;
	needsSync: boolean;
}

export interface ProductSyncStatus {
	totalInActindo: number;
	totalInNav: number;
	totalInMiddleware: number;
	synced: number;
	needsSync: number;
	mismatch: number;
	orphaned: number;
	items: ProductSyncItem[];
}

export interface CustomerSyncStatus {
	totalInMiddleware: number;
	totalInNav: number;
	synced: number;
	needsSync: number;
	items: CustomerSyncItem[];
}
