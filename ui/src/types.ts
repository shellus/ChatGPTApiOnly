export type Mode = "official" | "custom";
export interface Profile {
  id: string;
  name: string | null;
  official_auth?: string | null;
  account_id?: string | null;
  email?: string | null;
  account_name?: string | null;
  custom_key?: string | null;
  custom_model?: string | null;
  custom_effort?: string | null;
  model_providers_toml?: string | null;
}
export interface Fields {
  provider_name: string;
  base_url: string;
  api_key: string;
  model: string;
  effort: string;
}
export interface Library {
  official_accounts: Profile[];
  custom_providers: Profile[];
  selected_official: string;
  selected_custom: string;
  official_proxy_url?: string | null;
  official_model?: string | null;
  official_effort?: string | null;
}
export interface Draft {
  mode: Mode;
  library: Library;
  custom_fields: Record<string, Fields>;
}
export interface View {
  revision: string;
  draft: Draft;
  config_dir: string;
}
export interface Progress {
  phase: string;
  completed: number;
  total: number;
}
