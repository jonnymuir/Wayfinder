import type { NodeProps } from '@xyflow/react';
import { useGraphCallbacks } from '../graph-callbacks.js';
import type { StageFlowNode } from '../graph-model.js';
import { iconForStage } from '../node-icons.js';
import { ChangeBadge, changeDescription, NoteMarker, notesDescription } from './change-badge.js';
import { HandleFan } from './handle-fan.js';
import { NodeIcon } from './node-icon.js';

export function StageNode({ data }: NodeProps<StageFlowNode>) {
  const callbacks = useGraphCallbacks();
  const { node, rowRank, sourceHandles, targetHandles, selected, change, openNotes, readOnly } = data;
  const stage = node.stage;

  const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      callbacks.selectStage(stage.stageKey);
      return;
    }
    if (event.key.toLowerCase() === 'e') {
      event.preventDefault();
      callbacks.selectStage(stage.stageKey, { openInspector: true });
      return;
    }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      callbacks.requestDeleteStage(stage.stageKey, event.currentTarget);
      return;
    }
    if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
      event.preventDefault();
      const rect = event.currentTarget.getBoundingClientRect();
      callbacks.openContextMenu(
        { clientX: rect.left + rect.width / 2, clientY: rect.bottom },
        { kind: 'stage', stageKey: stage.stageKey },
        event.currentTarget
      );
    }
  };

  const handleContextMenu = (event: React.MouseEvent<HTMLButtonElement>) => {
    if (readOnly) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    callbacks.openContextMenu(
      { clientX: event.clientX, clientY: event.clientY },
      { kind: 'stage', stageKey: stage.stageKey },
      event.currentTarget
    );
  };

  const className = ['stage-node', node.surface, selected ? 'selected' : '', change ? `recently-${change}` : ''].filter(Boolean).join(' ');

  return (
    <div className="stage-node-shell" data-wayfinder-stage-card={stage.stageKey} data-wayfinder-row-rank={String(rowRank)}>
      <HandleFan handles={targetHandles} type="target" readOnly={readOnly} />
      <button
        type="button"
        className={className}
        aria-pressed={selected}
        aria-label={`${stage.displayName}, ${node.queueLabel} queue${changeDescription(change)}${notesDescription(openNotes)}`}
        data-wayfinder-stage={stage.stageKey}
        data-wayfinder-queue={node.queueKey}
        data-wayfinder-stage-change={change ?? undefined}
        data-wayfinder-stage-notes={openNotes || undefined}
        onClick={() => callbacks.selectStage(stage.stageKey)}
        onDoubleClick={() => callbacks.selectStage(stage.stageKey, { openInspector: true })}
        onKeyDown={handleKeyDown}
        onContextMenu={handleContextMenu}
      >
        <span className="node-header">
          <span className="node-icon-chip">
            <NodeIcon icon={iconForStage(stage)} />
          </span>
          <span className="node-meta">{stage.stageType}</span>
          <ChangeBadge change={change} />
          <NoteMarker count={openNotes} />
        </span>
        <span className="node-label">{stage.displayName}</span>
      </button>
      <HandleFan handles={sourceHandles} type="source" readOnly={readOnly} />
    </div>
  );
}
