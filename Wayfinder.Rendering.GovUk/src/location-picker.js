// Progressive enhancement for the location markup Wayfinder.Rendering.GovUk renders.
//
// A location-picker: the plain "latitude,longitude" text input always stays the working control. This
// adds a "use my current location" button and a map, kept in sync with that input. Without this script
// (or if the map fails) the page still works.
//
// A read-only map (a stat tile with display "map"): the point is already written out as text; this adds a
// map showing it, and removes the empty map box again if the value is not a point.
//
// Configuration comes from <meta> tags the host can add (all optional):
//   wayfinder-map-tile-url        a {z}/{x}/{y} tile template (default: public OpenStreetMap, demos only)
//   wayfinder-map-attribution     attribution for that tile source
//   wayfinder-map-default-centre  "latitude,longitude" the map opens on when there is no value or fix
//
// Each change fires a bubbling `wayfinder:location-changed` event on the input, detail:
//   { latitude, longitude, source: 'device' | 'map' | 'typed', accuracyMetres? }
// so a host can record where a position came from.
import 'ol/ol.css';
import './location-picker.css';
import Collection from 'ol/Collection.js';
import Feature from 'ol/Feature.js';
import OlMap from 'ol/Map.js';
import View from 'ol/View.js';
import Point from 'ol/geom/Point.js';
import Translate from 'ol/interaction/Translate.js';
import TileLayer from 'ol/layer/Tile.js';
import VectorLayer from 'ol/layer/Vector.js';
import { fromLonLat, toLonLat } from 'ol/proj.js';
import VectorSource from 'ol/source/Vector.js';
import XYZ from 'ol/source/XYZ.js';
import { Circle as CircleStyle, Fill, Stroke, Style } from 'ol/style.js';
import { formatPoint, parsePoint, wrapLongitude } from './location-point.mjs';

const OSM_TILES = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';
const OSM_ATTRIBUTION = '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors';
const FALLBACK_CENTRE = { latitude: 54, longitude: -2 };
const PIN_STYLE = new Style({
  image: new CircleStyle({
    radius: 9,
    fill: new Fill({ color: '#1d70b8' }),
    stroke: new Stroke({ color: '#ffffff', width: 3 }),
  }),
});

const meta = (name) => document.querySelector(`meta[name="wayfinder-map-${name}"]`)?.content || undefined;

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}

const tileLayer = () =>
  new TileLayer({ source: new XYZ({ url: meta('tile-url') ?? OSM_TILES, attributions: meta('attribution') ?? OSM_ATTRIBUTION }) });

const pinLayer = (marker) => new VectorLayer({ source: new VectorSource({ features: [marker] }), style: PIN_STYLE });

function loadStylesheet() {
  const href = new URL('wayfinder-location-picker.css', import.meta.url).href;
  if (document.querySelector(`link[href="${href}"]`)) return;
  const link = element('link');
  link.rel = 'stylesheet';
  link.href = href;
  document.head.append(link);
}

const GEOLOCATION_ERRORS = {
  1: 'Location permission was declined. Enter the location above, or select it on the map.',
  2: 'Your device could not work out its location. Enter it above, or select it on the map.',
  3: 'Finding your location took too long. Try again, enter it above, or select it on the map.',
};

function enhance(wrapper) {
  const input = wrapper.querySelector('[data-wayfinder-location-input]');
  if (!input || wrapper.dataset.wayfinderEnhanced) return;
  wrapper.dataset.wayfinderEnhanced = 'true';
  const label = wrapper.querySelector('label')?.textContent.trim() ?? 'location';

  const button = element('button', 'govuk-button govuk-button--secondary govuk-!-margin-bottom-0', 'Use my current location');
  button.type = 'button';
  button.dataset.module = 'govuk-button';
  const status = element('p', 'govuk-hint wayfinder-location__status');
  status.setAttribute('role', 'status');
  const mapElement = element('div', 'wayfinder-location__map');
  mapElement.tabIndex = 0;
  mapElement.setAttribute('role', 'group');
  mapElement.setAttribute(
    'aria-label',
    `Map for ${label}. Pan with the arrow keys. To set the location without a mouse or touch, type it in the field above.`,
  );
  wrapper.append(button, status, mapElement);
  loadStylesheet();

  const initial = parsePoint(input.value) ?? parsePoint(meta('default-centre')) ?? FALLBACK_CENTRE;
  const marker = new Feature();
  const view = new View({ center: fromLonLat([initial.longitude, initial.latitude]), zoom: parsePoint(input.value) ? 15 : 5 });
  const map = new OlMap({
    target: mapElement,
    view,
    layers: [tileLayer(), pinLayer(marker)],
  });

  const place = (point, centre) => {
    const coordinate = fromLonLat([point.longitude, point.latitude]);
    marker.setGeometry(new Point(coordinate));
    if (centre) view.setCenter(coordinate);
  };

  const commit = (point, source, accuracyMetres) => {
    input.value = formatPoint(point.latitude, point.longitude);
    place(point, source !== 'map');
    if (source === 'device') view.setZoom(Math.max(view.getZoom(), 15));
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new CustomEvent('wayfinder:location-changed', { bubbles: true, detail: { ...point, source, accuracyMetres } }));
  };

  const fromCoordinate = (coordinate) => {
    const [longitude, latitude] = toLonLat(coordinate);
    return { latitude: Math.max(-90, Math.min(90, latitude)), longitude: wrapLongitude(longitude) };
  };

  const locate = () => {
    if (!('geolocation' in navigator)) {
      status.textContent = 'Your device cannot share its location. Enter it above, or select it on the map.';
      button.hidden = true;
      return;
    }
    button.disabled = true;
    status.textContent = 'Finding your location…';
    navigator.geolocation.getCurrentPosition(
      ({ coords }) => {
        commit({ latitude: coords.latitude, longitude: coords.longitude }, 'device', coords.accuracy);
        status.textContent = `Location set from this device, accurate to about ${Math.round(coords.accuracy)} metres. Check the pin, or move it.`;
        button.disabled = false;
      },
      (error) => {
        status.textContent = GEOLOCATION_ERRORS[error.code] ?? GEOLOCATION_ERRORS[2];
        button.disabled = false;
      },
      { enableHighAccuracy: true, timeout: 15000, maximumAge: 30000 },
    );
  };

  const drag = new Translate({ features: new Collection([marker]) });
  map.addInteraction(drag);
  drag.on('translateend', () => {
    commit(fromCoordinate(marker.getGeometry().getCoordinates()), 'map');
    status.textContent = 'Location set from the map.';
  });
  map.on('singleclick', (event) => {
    commit(fromCoordinate(event.coordinate), 'map');
    status.textContent = 'Location set from the map.';
  });
  input.addEventListener('change', () => {
    const point = parsePoint(input.value);
    if (!point) return;
    place(point, true);
    input.dispatchEvent(new CustomEvent('wayfinder:location-changed', { bubbles: true, detail: { ...point, source: 'typed' } }));
  });
  button.addEventListener('click', locate);

  const existing = parsePoint(input.value);
  if (existing) place(existing, false);
  else if (!input.value.trim()) locate();
}

function show(wrapper) {
  if (wrapper.dataset.wayfinderEnhanced) return;
  const point = parsePoint(wrapper.dataset.wayfinderLocation);
  if (!point) {
    wrapper.remove();
    return;
  }
  wrapper.dataset.wayfinderEnhanced = 'true';
  const label = wrapper.dataset.wayfinderLabel || 'location';
  wrapper.tabIndex = 0;
  wrapper.setAttribute('role', 'group');
  wrapper.setAttribute('aria-label', `Map showing ${label} at ${formatPoint(point.latitude, point.longitude)}. Pan with the arrow keys.`);
  loadStylesheet();

  const coordinate = fromLonLat([point.longitude, point.latitude]);
  const marker = new Feature(new Point(coordinate));
  new OlMap({ target: wrapper, view: new View({ center: coordinate, zoom: 15 }), layers: [tileLayer(), pinLayer(marker)] });
}

document.querySelectorAll('[data-wayfinder-location-picker]').forEach(enhance);
document.querySelectorAll('[data-wayfinder-location-map]').forEach(show);
