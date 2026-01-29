"""
Simple test script to query the API.
Run with: python test_api.py
"""
import json
import urllib.request
import urllib.parse

BASE_URL = "http://localhost:5000"

def test_health():
    """Test health endpoint"""
    print("=" * 60)
    print("Testing /health endpoint...")
    try:
        with urllib.request.urlopen(f"{BASE_URL}/health") as response:
            data = json.loads(response.read().decode())
            print(f"Status: {response.status}")
            print(json.dumps(data, indent=2))
    except Exception as e:
        print(f"Error: {e}")
    print()

def test_query_events():
    """Query recent events"""
    print("=" * 60)
    print("Querying recent events...")
    payload = {
        "offset": 0,
        "limit": 10,
        "order_by": "timestamp",
        "order_desc": True
    }
    try:
        data = json.dumps(payload).encode('utf-8')
        req = urllib.request.Request(
            f"{BASE_URL}/api/events/query",
            data=data,
            headers={'Content-Type': 'application/json'}
        )
        with urllib.request.urlopen(req) as response:
            result = json.loads(response.read().decode())
            print(f"Status: {response.status}")
            print(f"Total events: {result.get('total', 0)}")
            print(json.dumps(result, indent=2))
    except Exception as e:
        print(f"Error: {e}")
    print()

def test_statistics():
    """Get statistics"""
    print("=" * 60)
    print("Getting statistics...")
    try:
        with urllib.request.urlopen(f"{BASE_URL}/api/events/statistics") as response:
            data = json.loads(response.read().decode())
            print(f"Status: {response.status}")
            print(json.dumps(data, indent=2))
    except urllib.error.HTTPError as e:
        print(f"Status: {e.code}")
        print(f"Error: {e.read().decode()}")
    except Exception as e:
        print(f"Error: {e}")
    print()

if __name__ == "__main__":
    print("\n=== DGT Backend API Test ===\n")

    try:
        test_health()
        test_query_events()
        test_statistics()

        print("=" * 60)
        print("Tests complete!")
        print("\nTo see live events, restart Excel and make some cell changes.")
        print("Then run this script again to see the new events.")

    except ConnectionRefusedError:
        print("❌ Error: Could not connect to backend at http://localhost:5000")
        print("   Make sure the backend is running with: python run_dev.py")
    except Exception as e:
        print(f"❌ Error: {e}")
