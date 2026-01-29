# Live Excel Model Tracking Dashboard

A simple, real-time web dashboard to visualize Excel governance tracking events captured by the Domino Governance Tracker backend.

## Features

- **Real-time Metrics**:  - Time since last update
  - Total database size
  - Total cell changes tracked
  - Total events tracked

- **Live Event Stream**: View recent events with details including:
  - Event type (Cell Change, Workbook Save, etc.)
  - User and machine information
  - Workbook and sheet context
  - Cell addresses and value changes
  - Formulas and additional details

- **Auto-refresh**: Dashboard automatically refreshes every 5 seconds to show new events

## Access the Dashboard

Once the backend server is running, access the dashboard at:

```
http://localhost:5000/dashboard
```

Or if deployed to Domino or another host:

```
https://your-domain.com/dashboard
```

## Technical Details

### Frontend
- Single-page HTML/CSS/JavaScript application
- No external dependencies - vanilla JavaScript
- Responsive design with animated event cards
- Auto-refresh with loading indicators

### Backend API
The dashboard consumes the `/api/dashboard/live-data` endpoint which provides:
- Aggregated metrics
- Recent events (default: 50, max: 500)
- Real-time data from SQLite database

### Files
- `static/index.html` - Complete dashboard UI (HTML + CSS + JS)
- `api/dashboard.py` - Dashboard API endpoint
- `main.py` - FastAPI static files mount configuration

## Customization

### Adjust Refresh Rate
Edit `static/index.html`, line with `REFRESH_INTERVAL`:

```javascript
const REFRESH_INTERVAL = 5000; // milliseconds (default: 5 seconds)
```

### Change Event Limit
Modify the API call in `static/index.html`:

```javascript
const response = await fetch(`${API_BASE}/api/dashboard/live-data?limit=100`);
```

### Customize Styling
All CSS is embedded in `static/index.html` within the `<style>` tags. Colors, fonts, and layout can be easily modified.

## Development

The dashboard is a static web application served by FastAPI's `StaticFiles` middleware. Changes to `static/index.html` are immediately visible (just refresh the browser).

## Troubleshooting

**Dashboard shows "No events recorded yet"**
- Ensure the Excel add-in is installed and running
- Check that events are being sent to the backend (check logs or use `/api/events/query`)
- Verify the database file exists at `backend/data/dgt.db`

**"Failed to fetch data from server" error**
- Check that the backend server is running
- Verify CORS settings allow requests from your domain
- Check browser console for detailed error messages

**Events not updating**
- Check browser console for JavaScript errors
- Verify the `/api/dashboard/live-data` endpoint is accessible
- Ensure auto-refresh hasn't been disabled by browser/extension

## Screenshots

The dashboard provides a clean, modern interface with:
- Purple gradient background
- White content cards with shadows
- Color-coded event types
- Smooth animations on event cards
- Clear metric displays
