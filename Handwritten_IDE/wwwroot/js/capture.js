// ---- Tunable constants (to be set during testing on the iPad) ----
const LINE_HEIGHT = 35;          // px per ruled row
const TOP_MARGIN = 10;           // px above the first row
const INITIAL_ROWS = 12;
const MIN_EMPTY_ROWS = 3;        // keep at least this many empty rows below the last used row
const ALLOWED_TYPES = ['pen'];   // stylus only for drawing
const ALLOW_FINGER_SCROLL = true;
const RECOGNIZE_DELAY_MS = 800;  // pause after the last pen-up before recognition runs

const area = document.getElementById('area');
const canvas = document.getElementById('ink');
const ctx = canvas.getContext('2d');
const statusEl = document.getElementById('status');
const lastEl = document.getElementById('lastStroke');
const rowSummaryEl = document.getElementById('rowSummary');
const recognizeStatusEl = document.getElementById('recognizeStatus');

const strokes = [];
let current = null;
let nextId = 1;
let rows = INITIAL_ROWS;
let touchScroll = null;

// ---- Recognition state ----
const dirtyRows = new Set();     // rows with strokes not yet recognized
const recognizedRows = {};       // row index -> recognized text
let recognizeTimer = null;
let recognizeInFlight = false;
let generation = 0;              // bumped on Clear so late responses are ignored

// ---- Layout ----
function canvasHeight() { return TOP_MARGIN + rows * LINE_HEIGHT; }

function resizeCanvas() {
    const dpr = window.devicePixelRatio || 1;
    const w = area.clientWidth;
    const h = canvasHeight();
    canvas.style.width = w + 'px';
    canvas.style.height = h + 'px';
    canvas.width = w * dpr;
    canvas.height = h * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    redraw();
}

function rowOfY(y) {
    const r = Math.floor((y - TOP_MARGIN) / LINE_HEIGHT);
    return Math.max(0, Math.min(rows - 1, r));
}

// Row where most of the stroke's points fall (ties go to the upper row)
function majorityRow(points) {
    const counts = new Map();
    for (const p of points) {
        const r = rowOfY(p.y);
        counts.set(r, (counts.get(r) || 0) + 1);
    }
    let best = -1, bestCount = -1;
    for (const [r, c] of counts) {
        if (c > bestCount || (c === bestCount && r < best)) { best = r; bestCount = c; }
    }
    return best;
}

// ---- Drawing ----
function drawRules() {
    const w = area.clientWidth;
    ctx.lineWidth = 1;
    ctx.strokeStyle = '#c8d4e3';
    ctx.fillStyle = '#9aa7b8';
    ctx.font = '11px sans-serif';
    for (let r = 0; r < rows; r++) {
        const y = TOP_MARGIN + (r + 1) * LINE_HEIGHT;
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(w, y);
        ctx.stroke();
        ctx.fillText(String(r + 1), 4, y - LINE_HEIGHT + 14);
    }
}

function drawStroke(points) {
    if (points.length === 0) return;
    ctx.beginPath();
    ctx.moveTo(points[0].x, points[0].y);
    for (let i = 1; i < points.length; i++) ctx.lineTo(points[i].x, points[i].y);
    if (points.length === 1) ctx.lineTo(points[0].x + 0.01, points[0].y);
    ctx.stroke();
}

function setInkStyle() {
    ctx.lineWidth = 2;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.strokeStyle = '#000';
}

function redraw() {
    ctx.clearRect(0, 0, area.clientWidth, canvasHeight());
    drawRules();
    setInkStyle();
    for (const s of strokes) drawStroke(s.points);
}

// ---- Data ----
function toPoint(e) {
    const rect = canvas.getBoundingClientRect(); // accounts for scrolling
    return {
        x: e.clientX - rect.left,
        y: e.clientY - rect.top,
        t: Math.round(e.timeStamp),
        pressure: e.pressure
    };
}

function updateSummary() {
    const counts = {};
    for (const s of strokes) counts[s.row] = (counts[s.row] || 0) + 1;
    const lines = Object.keys(counts)
        .map(Number).sort((a, b) => a - b)
        .map(r => `Row ${r + 1}: ${counts[r]} stroke(s)`);
    rowSummaryEl.textContent = lines.length ? lines.join('\n') : '(none yet)';
    statusEl.textContent = `Strokes: ${strokes.length} | Rows: ${rows}`;
}

// ---- Recognition ----
function scheduleRecognition() {
    clearTimeout(recognizeTimer);
    recognizeTimer = setTimeout(runRecognition, RECOGNIZE_DELAY_MS);
}

function renderRecognized() {
    const withText = Object.keys(recognizedRows).map(Number).filter(r => recognizedRows[r]);
    const last = withText.length ? Math.max(...withText) : -1;
    const lines = [];
    for (let r = 0; r <= last; r++) lines.push(recognizedRows[r] || '');
    editorApi.setLines(lines);
}

async function runRecognition() {
    if (recognizeInFlight) { scheduleRecognition(); return; }
    if (dirtyRows.size === 0) return;

    const rowsToSend = [...dirtyRows];
    dirtyRows.clear();
    const myGeneration = generation;
    recognizeInFlight = true;
    recognizeStatusEl.textContent = '(recognizing...)';

    try {
        const payload = { strokes: strokes.filter(s => rowsToSend.includes(s.row)) };
        const res = await fetch('/api/recognize', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const results = await res.json();

        if (myGeneration !== generation) return;   // Clear was pressed meanwhile
        for (const r of rowsToSend) recognizedRows[r] = '';
        for (const rr of results) recognizedRows[rr.row] = rr.text;
        renderRecognized();
        recognizeStatusEl.textContent = '';
    } catch (err) {
        // Put the rows back so the next pause retries them
        if (myGeneration === generation) {
            rowsToSend.forEach(r => dirtyRows.add(r));
            recognizeStatusEl.textContent = `(recognition failed: ${err.message})`;
        }
    } finally {
        recognizeInFlight = false;
        if (dirtyRows.size > 0 && myGeneration === generation) scheduleRecognition();
    }
}

// ---- Pointer handling ----
canvas.addEventListener('pointerdown', e => {
    if (e.pointerType === 'touch' && ALLOW_FINGER_SCROLL) {
        canvas.setPointerCapture(e.pointerId);
        touchScroll = { id: e.pointerId, lastY: e.clientY };
        return;
    }
    if (!ALLOWED_TYPES.includes(e.pointerType)) return;
    e.preventDefault();
    canvas.setPointerCapture(e.pointerId);
    setInkStyle();
    current = { id: nextId++, pointerType: e.pointerType, row: null, points: [toPoint(e)] };
});

canvas.addEventListener('pointermove', e => {
    if (touchScroll && e.pointerId === touchScroll.id) {
        area.scrollTop -= (e.clientY - touchScroll.lastY);
        touchScroll.lastY = e.clientY;
        return;
    }
    if (!current) return;
    const events = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
    const list = events.length ? events : [e];
    for (const ev of list) {
        const p = toPoint(ev);
        const prev = current.points[current.points.length - 1];
        current.points.push(p);
        ctx.beginPath();
        ctx.moveTo(prev.x, prev.y);
        ctx.lineTo(p.x, p.y);
        ctx.stroke();
    }
});

function endPointer(e) {
    if (touchScroll && e.pointerId === touchScroll.id) { touchScroll = null; return; }
    if (!current) return;
    current.row = majorityRow(current.points);
    strokes.push(current);
    lastEl.textContent = JSON.stringify(current, null, 1);

    // Grow the writing area so there are always empty rows below the last used one
    if (current.row + 1 + MIN_EMPTY_ROWS > rows) {
        rows = current.row + 1 + MIN_EMPTY_ROWS;
        resizeCanvas();
    }
    dirtyRows.add(current.row);
    scheduleRecognition();
    current = null;
    updateSummary();
}
canvas.addEventListener('pointerup', endPointer);
canvas.addEventListener('pointercancel', endPointer);

// ---- Prevent selection and context menu ----
['selectstart', 'contextmenu', 'dblclick'].forEach(type =>
    area.addEventListener(type, e => e.preventDefault())
);

document.addEventListener('selectstart', e => {
    const el = e.target.nodeType === 1 ? e.target : e.target.parentElement;
    if (!el || !el.closest('pre, #editor')) e.preventDefault();
});

// Block iOS zoom gestures on the writing area
document.addEventListener('gesturestart', e => e.preventDefault());

let lastTouchEnd = 0;
area.addEventListener('touchend', e => {
    const now = e.timeStamp;
    if (now - lastTouchEnd < 350) e.preventDefault();  // second tap of a double-tap
    lastTouchEnd = now;
}, { passive: false });

// ---- Buttons ----
document.getElementById('clearBtn').addEventListener('click', () => {
    strokes.length = 0;
    current = null;
    rows = INITIAL_ROWS;
    area.scrollTop = 0;
    resizeCanvas();
    lastEl.textContent = '(none yet)';
    generation++;
    clearTimeout(recognizeTimer);
    dirtyRows.clear();
    for (const k of Object.keys(recognizedRows)) delete recognizedRows[k];
    renderRecognized();
    recognizeStatusEl.textContent = '';
    updateSummary();
});

document.getElementById('exportBtn').addEventListener('click', () => {
    const data = { lineHeight: LINE_HEIGHT, topMargin: TOP_MARGIN, rows, strokes };
    const blob = new Blob([JSON.stringify(data, null, 1)], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'strokes.json';
    a.click();
    URL.revokeObjectURL(a.href);
});

window.addEventListener('resize', resizeCanvas);
resizeCanvas();
updateSummary();