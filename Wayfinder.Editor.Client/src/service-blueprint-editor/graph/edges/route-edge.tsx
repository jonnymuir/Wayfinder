import { useRef, useState } from 'react';
import { BaseEdge, EdgeLabelRenderer, Position, useReactFlow, type EdgeProps } from '@xyflow/react';
import { useGraphCallbacks } from '../graph-callbacks.js';
import type { RouteFlowEdge, TransitionChip } from '../graph-model.js';
import { buildCurvedRoutePath, buildCurvedWaypointPath } from './route-curve.js';

function chipClassName(chip: TransitionChip): string {
  return [
    'edge-chip',
    chip.branch ? 'branch-path' : '',
    chip.merge ? 'merge-path' : '',
    chip.selected ? 'selected' : '',
    chip.simulationPath ? 'simulation-path' : '',
  ].filter(Boolean).join(' ');
}

export function RouteEdge({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  markerEnd,
  data,
}: EdgeProps<RouteFlowEdge>) {
  const callbacks = useGraphCallbacks();
  const { screenToFlowPosition } = useReactFlow();
  const [dragPreview, setDragPreview] = useState<{ x: number; y: number } | null>(null);
  const draggingRef = useRef(false);
  if (!data) {
    return null;
  }
  const { edge, fromKey, toKey, simulationPath, chips, readOnly, manualWaypoint } = data;
  const source = { x: sourceX, y: sourceY };
  const target = { x: targetX, y: targetY };

  // When one route carries several transitions to the same target (e.g. approve/reject),
  // they'd otherwise draw as one shared line with only the labels stacked apart. Bend each
  // transition's own rail a little to either side of the shared line so the branches read as
  // distinct paths; the offset axis is whichever axis is perpendicular to the dominant
  // source→target direction, so a bend never runs parallel to the flow itself.
  const verticalFlow = (sourcePosition === Position.Top || sourcePosition === Position.Bottom)
    && (targetPosition === Position.Top || targetPosition === Position.Bottom);

  const path = buildCurvedRoutePath(source, sourcePosition, target, targetPosition, 0, verticalFlow);

  // An author-dragged bend point (or one being dragged right now) overrides the auto-computed
  // path entirely: a smooth curve constrained to pass exactly through the dropped point, rather
  // than the plain source→target curve.
  const activeWaypoint = dragPreview ?? manualWaypoint ?? null;
  const manualPath = activeWaypoint
    ? buildCurvedWaypointPath(source, sourcePosition, activeWaypoint, target, targetPosition)
    : null;
  const effectivePath = manualPath ?? path;

  const pathForChip = (chip: TransitionChip): string => {
    if (manualPath || chips.length <= 1 || !chip.railOffset) {
      return effectivePath;
    }
    return buildCurvedRoutePath(source, sourcePosition, target, targetPosition, chip.railOffset, verticalFlow);
  };

  const handleDragPointerDown = (event: React.PointerEvent<SVGPathElement>) => {
    if (readOnly) {
      return;
    }
    event.currentTarget.setPointerCapture(event.pointerId);
    draggingRef.current = true;
  };
  const handleDragPointerMove = (event: React.PointerEvent<SVGPathElement>) => {
    if (!draggingRef.current) {
      return;
    }
    setDragPreview(screenToFlowPosition({ x: event.clientX, y: event.clientY }));
  };
  const handleDragPointerUp = (event: React.PointerEvent<SVGPathElement>) => {
    if (!draggingRef.current) {
      return;
    }
    draggingRef.current = false;
    const flowPoint = screenToFlowPosition({ x: event.clientX, y: event.clientY });
    setDragPreview(null);
    callbacks.routeWaypointMoved(edge.key, flowPoint);
  };
  const handleDragDoubleClick = () => {
    if (readOnly || !manualWaypoint) {
      return;
    }
    callbacks.routeWaypointMoved(edge.key, null);
  };

  const basePathClass = [
    'edge-path',
    'route-rail',
    edge.backward ? 'loop-back' : '',
    simulationPath ? 'simulation-path' : '',
    manualWaypoint ? 'manually-routed' : '',
  ].filter(Boolean).join(' ');

  const handleChipKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>, chip: TransitionChip) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      callbacks.selectTransition(chip.index);
      return;
    }
    if (event.key.toLowerCase() === 'e') {
      event.preventDefault();
      callbacks.selectTransition(chip.index, { openInspector: true });
      return;
    }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault();
      callbacks.requestDeleteTransition(chip.index);
      return;
    }
    if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
      event.preventDefault();
      const rect = event.currentTarget.getBoundingClientRect();
      callbacks.openContextMenu(
        { clientX: rect.left + rect.width / 2, clientY: rect.bottom },
        { kind: 'transition', transitionIndex: chip.index },
        event.currentTarget
      );
    }
  };

  const handleChipContextMenu = (event: React.MouseEvent<HTMLButtonElement>, chip: TransitionChip) => {
    if (readOnly) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    callbacks.openContextMenu(
      { clientX: event.clientX, clientY: event.clientY },
      { kind: 'transition', transitionIndex: chip.index },
      event.currentTarget
    );
  };

  return (
    <>
      <BaseEdge
        id={id}
        path={effectivePath}
        markerEnd={markerEnd}
        className={basePathClass}
        data-wayfinder-route-path={edge.key}
        data-wayfinder-route-from={fromKey}
        data-wayfinder-route-to={toKey}
        data-wayfinder-route-simulation-path={String(simulationPath)}
      />
      {chips.map(chip => (
        <path
          key={`transition-path-${chip.index}`}
          d={pathForChip(chip)}
          fill="none"
          className={[
            'edge-path',
            'transition-overlay',
            chip.branch ? 'branch-path' : '',
            chip.merge ? 'merge-path' : '',
            chip.selected ? 'selected' : '',
            chip.simulationPath ? 'simulation-path' : '',
          ].filter(Boolean).join(' ')}
          data-wayfinder-transition-path={String(chip.index)}
          data-wayfinder-transition-from={chip.fromKey}
          data-wayfinder-transition-to={chip.toKey}
          data-wayfinder-transition-simulation-path={String(chip.simulationPath)}
        />
      ))}
      {!readOnly && (
        // Invisible, wide hit target over the whole rendered curve: the route itself is the
        // drag surface, not a separate handle. `nopan` is load-bearing — React Flow's own
        // pan-on-drag is wired natively on the pane element, which sits between this path and
        // where React's synthetic events are dispatched from, so calling stopPropagation() in
        // a React handler here fires too late to stop it (the pane's native listener has
        // already run by then). `nopan` is xyflow's own supported mechanism for exactly this:
        // it's checked before panning starts, not raced against via propagation.
        <path
          d={effectivePath}
          className="nopan route-drag-surface"
          fill="none"
          stroke="transparent"
          strokeWidth={16}
          style={{ pointerEvents: 'stroke', cursor: dragPreview ? 'grabbing' : 'grab' }}
          aria-label={manualWaypoint
            ? 'Drag to move this route’s bend point. Double-click to reset to the automatic path.'
            : 'Drag to bend this route.'}
          data-wayfinder-route-drag-surface={edge.key}
          onPointerDown={handleDragPointerDown}
          onPointerMove={handleDragPointerMove}
          onPointerUp={handleDragPointerUp}
          onDoubleClick={handleDragDoubleClick}
        />
      )}
      <EdgeLabelRenderer>
        {chips.map(chip => (
          <button
            key={`chip-${chip.index}`}
            type="button"
            className={chipClassName(chip)}
            style={{
              position: 'absolute',
              transform: `translate(-50%, -50%) translate(${chip.x}px, ${chip.y}px)`,
              pointerEvents: 'all',
            }}
            aria-label={chip.ariaLabel}
            data-wayfinder-transition={String(chip.index)}
            data-wayfinder-transition-from={chip.fromKey}
            data-wayfinder-transition-to={chip.toKey}
            data-wayfinder-transition-simulation-path={String(chip.simulationPath)}
            onClick={() => callbacks.selectTransition(chip.index)}
            onDoubleClick={() => callbacks.selectTransition(chip.index, { openInspector: true })}
            onKeyDown={event => handleChipKeyDown(event, chip)}
            onContextMenu={event => handleChipContextMenu(event, chip)}
          >
            {chip.label}
          </button>
        ))}
      </EdgeLabelRenderer>
    </>
  );
}
