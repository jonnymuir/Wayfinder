import { html, svg } from 'lit';
import { NODE_ICONS, type NodeIconDef, type NodeIconName } from '../graph/node-icons.js';

function renderNodeIconSvg(icon: NodeIconDef) {
  return svg`
    <svg viewBox=${icon.viewBox} width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      ${icon.paths.map((d) => svg`<path d=${d}></path>`)}
    </svg>
  `;
}

/** Small icon-glyph button row — the curated set is deliberately short (see graph/node-icons.ts) so a full grid/search UI isn't needed. */
export function renderIconPicker(selected: NodeIconName, onPick: (icon: NodeIconName) => void) {
  return html`
    <div class="icon-picker" role="radiogroup" aria-label="Icon">
      ${(Object.keys(NODE_ICONS) as NodeIconName[]).map(
        (name) => html`
        <button
          type="button"
          class="icon-picker-option ${name === selected ? 'icon-picker-option-selected' : ''}"
          role="radio"
          aria-checked=${name === selected}
          aria-label=${name}
          title=${name}
          data-wayfinder-icon-option=${name}
          @click=${() => onPick(name)}
        >
          ${renderNodeIconSvg(NODE_ICONS[name])}
        </button>
      `
      )}
    </div>
  `;
}
