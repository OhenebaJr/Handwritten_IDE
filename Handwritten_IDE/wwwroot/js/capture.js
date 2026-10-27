const canvas = document.getElementById('ink');
const ctx = canvas.getContext('2d');
const statusEl = document.getElementById('status');
const lastEl = document.getElementById('lastStroke');

const ALLOWED_TYPES = ['pen'];   // stylus only
const strokes = [];              // completed strokes
let current = null;              // stroke being drawn
let nextId = 1;

function resizeCanvas() {
    const dpr = window.devicePixelRatio || 1;
    const rect = canvas.getBoundingClientRect();
    canvas.width = rect.width * dpr;
    canvas.height = rect.height * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.lineWidth = 2;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.strokeStyle = '#000';
    redraw();
}

function redraw() {
    const rect = canvas.getBoundingClientRect();
    ctx.clearRect(0, 0, rect.width, rect.height);
    for (const s of strokes) drawStroke(s.points);
}

function drawStroke(points) {
    if (points.length === 0) return;
    ctx.beginPath();
    ctx.moveTo(points[0].x, points[0].y);
    for (let i = 1; i < points.length; i++) ctx.lineTo(points[i].x, points[i].y);
    if (points.length === 1) ctx.lineTo(points[0].x + 0.01, points[0].y); // dot
    ctx.stroke();
}

function toPoint(e) {
    const rect = canvas.getBoundingClientRect();
    return {
        x: e.clientX - rect.left,
        y: e.clientY - rect.top,
        t: Math.round(e.timeStamp),
        pressure: e.pressure
    };
}

canvas.addEventListener('pointerdown', e => {
    if (!ALLOWED_TYPES.includes(e.pointerType)) return;
    e.preventDefault();
    canvas.setPointerCapture(e.pointerId);
    current = { id: nextId++, pointerType: e.pointerType, points: [toPoint(e)] };
});

canvas.addEventListener('pointermove', e => {
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

function endStroke(e) {
    if (!current) return;
    strokes.push(current);
    lastEl.textContent = JSON.stringify(current, null, 1);
    statusEl.textContent = `Strokes: ${strokes.length}`;
    current = null;
}
canvas.addEventListener('pointerup', endStroke);
canvas.addEventListener('pointercancel', endStroke);

document.getElementById('clearBtn').addEventListener('click', () => {
    strokes.length = 0;
    current = null;
    redraw();
    statusEl.textContent = 'Strokes: 0';
    lastEl.textContent = '(none yet)';
});

document.getElementById('exportBtn').addEventListener('click', () => {
    const blob = new Blob([JSON.stringify({ strokes }, null, 1)], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = 'strokes.json';
    a.click();
    URL.revokeObjectURL(a.href);
});

window.addEventListener('resize', resizeCanvas);
resizeCanvas();