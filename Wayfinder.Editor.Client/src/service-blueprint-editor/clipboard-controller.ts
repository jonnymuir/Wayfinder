import type { ReactiveControllerHost } from 'lit';
import { BlueprintClipboard, type ClipboardContext } from './blueprint-clipboard.js';
import type { EditorSelection } from './editor-selection.js';
import type { ServiceBlueprint } from './types.js';

/** What copy and paste need from the editor, read afresh each time. */
export interface ClipboardInputs {
  context: () => ClipboardContext;
  selection: () => EditorSelection;
  /** Apply a paste as an ordinary, undoable edit. */
  commit: (next: ServiceBlueprint, selection: EditorSelection) => void;
  chooseStageAction: (index: number) => void;
  toast: (message: string) => void;
  revealInspector: () => void;
}

/** Copy and paste in the editor: what the clipboard holds, and what pasting it does to the blueprint and selection. */
export class ClipboardController {
  private readonly clipboard = new BlueprintClipboard();

  constructor(
    private readonly host: ReactiveControllerHost,
    private readonly inputs: ClipboardInputs
  ) {}

  get canCopy(): boolean {
    return this.clipboard.canCopy(this.inputs.context());
  }

  get canPaste(): boolean {
    return this.clipboard.canPaste(this.inputs.context());
  }

  get summary(): string {
    return this.clipboard.summary;
  }

  copy(): boolean {
    const message = this.clipboard.copy(this.inputs.context());
    if (!message) {
      return false;
    }
    this.inputs.toast(message);
    this.host.requestUpdate();
    return true;
  }

  paste(): boolean {
    const outcome = this.clipboard.paste(this.inputs.context());
    if (!outcome.ok) {
      if (outcome.message) {
        this.inputs.toast(outcome.message);
      }
      return false;
    }

    const selection: EditorSelection = outcome.selectStageKey
      ? { kind: 'stage', stageKey: outcome.selectStageKey }
      : this.inputs.selection();
    this.inputs.commit(outcome.blueprint, selection);
    if (outcome.selectActionIndex !== undefined) {
      this.inputs.chooseStageAction(outcome.selectActionIndex);
    }
    this.inputs.toast(outcome.message);
    if (outcome.revealInspector) {
      this.inputs.revealInspector();
    }
    return true;
  }
}
