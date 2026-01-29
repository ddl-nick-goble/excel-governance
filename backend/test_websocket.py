"""Quick test to verify WebSocket endpoint works."""
import asyncio
import websockets
import json

async def test_websocket():
    uri = "ws://localhost:5000/api/dashboard/ws"
    print(f"Connecting to {uri}...")

    try:
        async with websockets.connect(uri) as websocket:
            print("Connected!")

            # Wait for initial message
            print("Waiting for initial data...")
            message = await websocket.recv()
            data = json.loads(message)

            print(f"\nReceived message type: {data['type']}")
            print(f"Total events: {data['metrics']['total_events']}")
            print(f"Number of events in payload: {len(data['events'])}")

            if data['events']:
                print(f"\nMost recent event:")
                event = data['events'][0]
                print(f"  Type: {event['event_type_display']}")
                print(f"  User: {event['user_name']}")
                print(f"  Workbook: {event['workbook_name']}")
                print(f"  Timestamp: {event['timestamp']}")

            print("\nWebSocket test successful!")
            print("Keeping connection open for 10 seconds to test for updates...")

            # Wait for any updates
            try:
                await asyncio.wait_for(websocket.recv(), timeout=10.0)
                print("Received an update!")
            except asyncio.TimeoutError:
                print("No updates received (this is normal if no new Excel activity)")

    except Exception as e:
        print(f"Error: {e}")
        return False

    return True

if __name__ == "__main__":
    result = asyncio.run(test_websocket())
    exit(0 if result else 1)
