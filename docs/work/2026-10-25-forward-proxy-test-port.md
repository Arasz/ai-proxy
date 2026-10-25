# Forward proxy test listener port

The `build-bdd` job in [run 40185780250](https://github.com/Arasz/ai-proxy/actions/runs/40185780250) failed one of 674 tests. `ForwardProxyTests.An_unknown_http_destination_passes_through_with_its_request_and_response` could not start Titanium's listener because its address was already in use.

The fixture asked a temporary listener for a free port, stopped it, then gave that port to Titanium. Another listener could bind in between. The fix asks Titanium to bind port zero directly and reads the assigned port from the started endpoint. The operating system selects and reserves that port in one operation. This changes only the test fixture; runtime proxy configuration is unchanged.

## Evidence

The regression starts a competing listener immediately before Titanium binds and keeps it alive through a real forwarded request. With the original reservation sequence, it deterministically failed with the same `Address already in use` exception. With direct port-zero binding, the competing listener and proxy have different nonzero ports and the request reaches the upstream with the expected response.

The scoped `ForwardProxyTests` run passed both tests. Titanium.Web.Proxy 7.0.26 reports its assigned port through `ProxyEndPoint.Port` after startup. The fixture disposes its service and temporary directory after startup failures as well as successful tests. System proxy configuration and root-certificate trust stay disabled.

Task graph: `aip-isolate-forward-proxy-test-port`. This test-only correction has no runtime release or version bump.
