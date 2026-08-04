import { Position } from '@xyflow/react';

const CURVE_EXTENSION_RATIO = 0.25;
const MIN_CURVE_EXTENSION = 24;

const DIRECTION_BY_POSITION: Record<Position, { dx: number; dy: number }> = {
  [Position.Top]: { dx: 0, dy: -1 },
  [Position.Bottom]: { dx: 0, dy: 1 },
  [Position.Left]: { dx: -1, dy: 0 },
  [Position.Right]: { dx: 1, dy: 0 },
};

type Point = { x: number; y: number };

function extensionFor(distance: number): number {
  return Math.max(distance * CURVE_EXTENSION_RATIO, MIN_CURVE_EXTENSION);
}

/** A control point pulled outward from an anchor along its handle's facing direction, plus an optional lateral offset used to fan several curves apart. */
function anchorControlPoint(
  anchor: Point,
  position: Position,
  extension: number,
  lateralOffset: number,
  verticalFlow: boolean
): Point {
  const { dx, dy } = DIRECTION_BY_POSITION[position];
  return {
    x: anchor.x + dx * extension + (verticalFlow ? lateralOffset : 0),
    y: anchor.y + dy * extension + (verticalFlow ? 0 : lateralOffset),
  };
}

/**
 * A single cubic bezier from source to target, control points anchored to
 * each end's facing direction (Top/Bottom/Left/Right) so the curve leaves
 * and arrives perpendicular to the node edge, same as the elbow paths this
 * replaced. `lateralOffset` shifts *both* control points sideways together
 * (perpendicular to the dominant flow axis) — used when several transitions
 * share one route (e.g. approve/reject to the same target): because both
 * ends move, not just a midpoint, the curves fan apart immediately from the
 * shared anchor instead of running coincident before crossing, which is what
 * an offset *elbow* path did (the source of the "X" crossing artifact).
 */
export function buildCurvedRoutePath(
  source: Point,
  sourcePosition: Position,
  target: Point,
  targetPosition: Position,
  lateralOffset: number,
  verticalFlow: boolean
): string {
  const extension = extensionFor(Math.hypot(target.x - source.x, target.y - source.y));
  const c1 = anchorControlPoint(source, sourcePosition, extension, lateralOffset, verticalFlow);
  const c2 = anchorControlPoint(target, targetPosition, extension, lateralOffset, verticalFlow);
  return `M ${source.x},${source.y} C ${c1.x},${c1.y} ${c2.x},${c2.y} ${target.x},${target.y}`;
}

/**
 * Two chained cubic beziers passing exactly through `waypoint` (an
 * author-dragged bend point), tangent-continuous at the join so the curve
 * reads as one smooth line rather than two segments meeting at a visible
 * corner. The shared tangent direction at the waypoint is the overall
 * source→target direction — the standard Catmull-Rom-style choice for a
 * smooth curve constrained to pass through a middle point — while each end
 * still anchors to its node's facing direction, same as buildCurvedRoutePath.
 */
export function buildCurvedWaypointPath(
  source: Point,
  sourcePosition: Position,
  waypoint: Point,
  target: Point,
  targetPosition: Position
): string {
  const overallDistance = Math.hypot(target.x - source.x, target.y - source.y);
  const tangent = overallDistance === 0
    ? { x: 0, y: 0 }
    : { x: (target.x - source.x) / overallDistance, y: (target.y - source.y) / overallDistance };

  const extension1 = extensionFor(Math.hypot(waypoint.x - source.x, waypoint.y - source.y));
  const c1 = anchorControlPoint(source, sourcePosition, extension1, 0, false);
  const c2 = { x: waypoint.x - tangent.x * extension1, y: waypoint.y - tangent.y * extension1 };

  const extension2 = extensionFor(Math.hypot(target.x - waypoint.x, target.y - waypoint.y));
  const c3 = { x: waypoint.x + tangent.x * extension2, y: waypoint.y + tangent.y * extension2 };
  const c4 = anchorControlPoint(target, targetPosition, extension2, 0, false);

  return `M ${source.x},${source.y} `
    + `C ${c1.x},${c1.y} ${c2.x},${c2.y} ${waypoint.x},${waypoint.y} `
    + `C ${c3.x},${c3.y} ${c4.x},${c4.y} ${target.x},${target.y}`;
}
