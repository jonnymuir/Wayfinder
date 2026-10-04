import type { ReactiveController, ReactiveControllerHost } from 'lit';
import type { ActionCatalogEntry, ComponentDescriptor, SupportSystemDescriptor } from './types.js';
import { type ServiceBlueprintActionCatalog, BuiltInServiceBlueprintActionCatalog } from './action-catalog.js';
import { type ServiceBlueprintComponentCatalog, HttpServiceBlueprintComponentCatalog } from './component-catalog.js';
import { type ServiceBlueprintSupportSystemCatalog, HttpServiceBlueprintSupportSystemCatalog } from './support-system-catalog.js';

/** Host-supplied overrides for the three catalogs; each falls back to its default when unset. */
export interface CatalogSources {
  actions(): ServiceBlueprintActionCatalog | undefined;
  components(): ServiceBlueprintComponentCatalog | undefined;
  supportSystems(): ServiceBlueprintSupportSystemCatalog | undefined;
}

/**
 * The action, component and support-system catalogs the editor offers from. Action types default to
 * Wayfinder's built-in list; the other two are fetched live from whichever host the editor talks to,
 * so a host-registered custom type appears with no editor change. If a live fetch fails (no host: an
 * offline demo, a Storybook story with no override) that catalog is simply empty and the UI that
 * needs it degrades gracefully — it never blocks the rest of the editor.
 */
export class EditorCatalogsController implements ReactiveController {
  actions: ActionCatalogEntry[] = [];
  components: ComponentDescriptor[] = [];
  supportSystems: SupportSystemDescriptor[] = [];

  constructor(
    private readonly _host: ReactiveControllerHost,
    private readonly _sources: CatalogSources,
    private readonly _onChanged: (changed: 'actions' | 'components' | 'supportSystems') => void
  ) {
    _host.addController(this);
  }

  hostConnected(): void {
    void this._loadActions();
    void this._loadComponents();
    void this._loadSupportSystems();
  }

  private async _loadActions() {
    const catalog = this._sources.actions() ?? new BuiltInServiceBlueprintActionCatalog();
    this.actions = await catalog.entries();
    this._changed('actions');
  }

  private async _loadComponents() {
    const catalog = this._sources.components() ?? new HttpServiceBlueprintComponentCatalog();
    this.components = await catalog.entries().catch(() => []);
    this._changed('components');
  }

  private async _loadSupportSystems() {
    const catalog = this._sources.supportSystems() ?? new HttpServiceBlueprintSupportSystemCatalog();
    this.supportSystems = await catalog.entries().catch(() => []);
    this._changed('supportSystems');
  }

  private _changed(which: 'actions' | 'components' | 'supportSystems') {
    this._host.requestUpdate();
    this._onChanged(which);
  }
}
