// Generic live-form runtime. Progressive enhancement for any stage whose definition declares a
// calculations block:
//
//  - reads the embedded live model ([data-wayfinder-live-model], emitted by
//    GovUkComponentRenderer.RenderForm whenever ServiceRequestResponseEnvelope.Render.Data["live"]
//    is present — see ProcessManagerEngine.BuildLiveModel, which every host gets for free, not
//    something this script or any host computes itself): the calculation set, input
//    types/defaults and service-sourced values the server evaluated with,
//  - listens to the stage's ordinary form controls (field:{key} inputs) and re-evaluates the
//    same declarative definitions via wayfinder-calculations.js on every change,
//  - updates whatever declares a binding: stat cards ([data-wayfinder-stat-field]),
//    charts ([data-wayfinder-chart]), slider value readouts ([data-wayfinder-slider]), and
//    visibility wrappers ([data-wayfinder-show-when]).
//
// It contains no domain knowledge and no layout: the service blueprint JSON decides what exists
// on the page; this runtime only keeps it live between submits. The server re-evaluates the
// identical definitions authoritatively on every render — this is a preview accelerator, never
// the thing anything is actually persisted or branched on.
//
// Ported from Umbraco.Prism's own prism-live-form.ts (same runtime, in a different repo, tied to
// Umbraco.Prism's own `fields[key]` form-naming convention) — adapted to this package's
// `field:{key}` convention (GovUk.FieldName) and its own no-build-step plain-JS convention
// (wayfinder-slider.js, wayfinder-live-recalculate.js, wayfinder-calculations.js).
import { Dec, evaluateCalculations, evaluateExpression, toScope } from './wayfinder-calculations.js';

const gbp = new Intl.NumberFormat('en-GB', {
  style: 'currency',
  currency: 'GBP',
  maximumFractionDigits: 0,
});

function formatValue(value, format) {
  if (value instanceof Dec) {
    return format?.toLowerCase() === 'gbp' ? gbp.format(value.toNumber()) : value.toString();
  }

  return String(value);
}

function boot() {
  const modelScript = document.querySelector('script[data-wayfinder-live-model]');
  if (!modelScript?.textContent) {
    return;
  }

  let model;
  try {
    model = JSON.parse(modelScript.textContent);
  } catch {
    return;
  }
  if (!model?.calculations?.fields) {
    return;
  }

  const form = modelScript.closest('form') ?? document.querySelector('form') ?? document;
  const serviceScope = toScope(model.service ?? {});

  const readInput = (key) => {
    const controls = form.querySelectorAll(`[name="field:${key}"]`);
    let raw = null;
    for (const control of controls) {
      if (control instanceof HTMLInputElement && (control.type === 'radio' || control.type === 'checkbox')) {
        if (control.checked) {
          raw = control.value;
          break;
        }
      } else {
        raw = control.value;
        break;
      }
    }

    if (raw === null || raw === '') {
      raw = model.defaults[key] ?? null;
    }

    const type = model.inputTypes[key];

    if (raw === null) {
      // Absent (nothing typed/ticked yet, no declared default) isn't the same as unknown — the
      // field is genuinely declared on this stage, it just has no value in the browser right now.
      // A number has no safe placeholder (0 is a real, meaningful value), so it stays out of scope
      // and any expression referencing it bare simply doesn't evaluate yet (see the catch in
      // update(), which leaves server-rendered values until it can). String/boolean fields DO have
      // a safe "nothing here" value — an empty text box already means "" and an unticked checkbox
      // already means false everywhere else in this system — matching CalculationScopeBuilder.Build's
      // server-side rule (Wayfinder/Services/Calculations/CalculationScopeBuilder.cs).
      if (type === 'number') {
        return undefined;
      }
      return type === 'boolean' ? false : '';
    }

    if (type === 'number') {
      const cleaned = raw.replace(/£|,/g, '').trim();
      return /^-?\d+(\.\d+)?$/.test(cleaned) ? Dec.fromString(cleaned) : undefined;
    }

    if (type === 'boolean') {
      // A checked GOV.UK checkbox's own value="true" (see GovUkFields.RenderBoolean) — or a
      // string default authored the same way — needs coercing to a real boolean the same way
      // CalculationScopeBuilder.Build does server-side; toBool() in wayfinder-calculations.js
      // requires an actual boolean, not this string.
      if (raw === 'true' || raw === 'True') return true;
      if (raw === 'false' || raw === 'False') return false;
      return raw;
    }

    return raw;
  };

  const collectScope = () => {
    const scope = { ...serviceScope };
    for (const key of Object.keys(model.inputTypes)) {
      const value = readInput(key);
      if (value !== undefined) {
        scope[key] = value;
      }
    }

    return scope;
  };

  const update = () => {
    let scope;
    let output;
    try {
      scope = collectScope();
      output = evaluateCalculations(model.calculations, scope);
    } catch (error) {
      console.warn('wayfinder-live-form: evaluation failed; leaving server-rendered values', error);
      return;
    }

    const fullScope = { ...scope, ...output.fields };

    // Stat cards (and anything else bound to a calculated field).
    document.querySelectorAll('[data-wayfinder-stat-field]').forEach((card) => {
      const fieldKey = card.dataset.wayfinderStatField;
      const value = output.fields[fieldKey];
      if (value === undefined) {
        return;
      }

      const format = model.calculations.fields[fieldKey]?.format;
      card.querySelector('.wayfinder-stat-card__value')?.replaceChildren(formatValue(value, format));
    });

    // Visibility wrappers.
    document.querySelectorAll('[data-wayfinder-show-when]').forEach((wrapper) => {
      const expression = wrapper.dataset.wayfinderShowWhen;
      try {
        const visible = evaluateExpression(expression, fullScope, model.calculations) !== false;
        wrapper.hidden = !visible;
      } catch {
        wrapper.hidden = false;
      }
    });

    // Charts.
    document.querySelectorAll('[data-wayfinder-chart]').forEach((figure) => {
      rebuildChart(figure, output.series);
    });
  };

  const updateSliderReadout = (input) => {
    const wrapper = input.closest('[data-wayfinder-slider]');
    const readout = wrapper?.querySelector('[data-wayfinder-slider-value]');
    if (readout) {
      readout.textContent = `${readout.dataset.prefix ?? ''}${input.value}${readout.dataset.suffix ?? ''}`;
    }
  };

  form.addEventListener('input', (event) => {
    const target = event.target;
    if (target instanceof HTMLInputElement && target.matches('[data-wayfinder-slider-input]')) {
      updateSliderReadout(target);
    }

    if (target.matches('[name^="field:"]')) {
      update();
    }
  });

  form.addEventListener('change', (event) => {
    if (event.target.matches('[name^="field:"]')) {
      update();
    }
  });
}

function rebuildChart(figure, series) {
  const configScript = figure.querySelector('script[data-wayfinder-chart-config]');
  if (!configScript?.textContent) {
    return;
  }

  let config;
  try {
    config = JSON.parse(configScript.textContent);
  } catch {
    return;
  }

  const rows = series[config.series];
  if (!rows) {
    return;
  }

  // Same validated categorical palette the server-side renderer uses (GovUkComponents.RenderChart).
  const palette = ['#4f46e5', '#0d9488', '#b45309', '#6d28d9'];
  const bands = config.bands.map((band, index) => ({
    ...band,
    color: band.color ?? palette[index % palette.length],
  }));

  const numeric = rows.map((row) => ({
    x: row[config.x] instanceof Dec ? row[config.x].toNumber() : 0,
    values: bands.map((band) => (row[band.key] instanceof Dec ? row[band.key].toNumber() : 0)),
  }));

  const maxTotal = Math.max(1, ...numeric.map((row) => row.values.reduce((a, b) => a + b, 0)));
  const plotHeight = 160;

  const plot = figure.querySelector('[data-wayfinder-chart-plot]');
  if (plot) {
    plot.replaceChildren(
      ...numeric.map((row) => {
        const bar = document.createElement('div');
        bar.className = 'wayfinder-chart__bar';
        bar.title = `${config.x} ${row.x}: ${row.values.reduce((a, b) => a + b, 0).toLocaleString('en-GB')}`;
        row.values.forEach((value, i) => {
          const segment = document.createElement('div');
          segment.style.height = `${Math.round((value / maxTotal) * plotHeight * 10) / 10}px`;
          segment.style.background = bands[i].color;
          bar.appendChild(segment);
        });
        return bar;
      }),
    );
  }

  const labels = figure.querySelector('[data-wayfinder-chart-labels]');
  if (labels) {
    labels.replaceChildren(
      ...numeric.map((row) => {
        const span = document.createElement('span');
        span.textContent = row.x % config.xLabelEvery === 0 ? String(row.x) : '';
        return span;
      }),
    );
  }

  const tableBody = figure.querySelector('[data-wayfinder-chart-table] tbody');
  if (tableBody) {
    tableBody.replaceChildren(
      ...numeric
        .filter((row, index) => index === 0 || row.x % config.xLabelEvery === 0)
        .map((row) => {
          const tr = document.createElement('tr');
          const th = document.createElement('th');
          th.scope = 'row';
          th.textContent = String(row.x);
          tr.appendChild(th);
          row.values.forEach((value) => {
            const td = document.createElement('td');
            td.textContent = value.toLocaleString('en-GB');
            tr.appendChild(td);
          });
          return tr;
        }),
    );
  }
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}
