from __future__ import annotations

import time
from typing import Any, Optional

import requests
import streamlit as st


# ---------------------------------------------------------
# Application configuration
# ---------------------------------------------------------

BACKEND_URL = (
    "http://localhost:5192/"
    "api/v1/documentation/generate"
)

CONNECT_TIMEOUT_SECONDS = 5
READ_TIMEOUT_SECONDS = 160
MAX_INPUT_CHARACTERS = 20_000


# ---------------------------------------------------------
# Custom application error
# ---------------------------------------------------------

class DocuMintError(Exception):
    def __init__(
        self,
        code: str,
        message: str,
        request_id: str = "",
        retryable: bool = False,
        status_code: Optional[int] = None,
    ) -> None:
        super().__init__(message)

        self.code = code
        self.message = message
        self.request_id = request_id
        self.retryable = retryable
        self.status_code = status_code


# ---------------------------------------------------------
# Utility functions
# ---------------------------------------------------------

def normalize_path(path: str) -> str:
    """Ensure every API route begins with a forward slash."""

    cleaned_path = path.strip()

    if not cleaned_path:
        return "/"

    if cleaned_path.startswith("/"):
        return cleaned_path

    return f"/{cleaned_path}"


def shorten_model_name(model_name: str) -> str:
    """Display a compact model name in the metrics section."""

    return model_name.replace("gemini-", "")


def endpoint_label(endpoint: dict[str, Any]) -> str:
    """Create a readable endpoint selector label."""

    method = str(
        endpoint.get("method", "UNKNOWN")
    ).upper()

    path = normalize_path(
        str(endpoint.get("path", "/"))
    )

    return f"{method}  {path}"


# ---------------------------------------------------------
# Backend communication
# ---------------------------------------------------------

def call_backend(raw_code: str) -> dict[str, Any]:
    """Send controller code to the DocuMint .NET backend."""

    try:
        response = requests.post(
            BACKEND_URL,
            json={"rawCode": raw_code},
            timeout=(
                CONNECT_TIMEOUT_SECONDS,
                READ_TIMEOUT_SECONDS,
            ),
        )

    except requests.exceptions.ConnectTimeout as exc:
        raise DocuMintError(
            code="BACKEND_CONNECT_TIMEOUT",
            message=(
                "DocuMint could not connect to the backend "
                "within the expected time."
            ),
            retryable=True,
        ) from exc

    except requests.exceptions.ReadTimeout as exc:
        raise DocuMintError(
            code="FRONTEND_TIMEOUT",
            message=(
                "The documentation request took too long. "
                "The AI service may be temporarily overloaded."
            ),
            retryable=True,
        ) from exc

    except requests.exceptions.ConnectionError as exc:
        raise DocuMintError(
            code="BACKEND_UNAVAILABLE",
            message=(
                "The DocuMint backend is unavailable. "
                "Confirm that the .NET server is running "
                "on port 5192."
            ),
            retryable=True,
        ) from exc

    except requests.exceptions.RequestException as exc:
        raise DocuMintError(
            code="NETWORK_ERROR",
            message=(
                "A network error occurred while contacting "
                "the DocuMint backend."
            ),
            retryable=True,
        ) from exc

    try:
        payload = response.json()

    except ValueError as exc:
        raise DocuMintError(
            code="NON_JSON_SERVER_RESPONSE",
            message="The backend returned an unreadable response.",
            retryable=response.status_code >= 500,
            status_code=response.status_code,
        ) from exc

    if not response.ok:
        if isinstance(payload, dict):
            raise DocuMintError(
                code=str(
                    payload.get(
                        "code",
                        "UNKNOWN_SERVER_ERROR",
                    )
                ),
                message=str(
                    payload.get(
                        "message",
                        "The request could not be completed.",
                    )
                ),
                request_id=str(
                    payload.get(
                        "requestId",
                        "",
                    )
                ),
                retryable=bool(
                    payload.get(
                        "retryable",
                        False,
                    )
                ),
                status_code=response.status_code,
            )

        raise DocuMintError(
            code="UNKNOWN_SERVER_ERROR",
            message="The server returned an unexpected error.",
            retryable=response.status_code >= 500,
            status_code=response.status_code,
        )

    if not isinstance(payload, dict):
        raise DocuMintError(
            code="INVALID_RESPONSE_STRUCTURE",
            message=(
                "The backend returned an unexpected "
                "response structure."
            ),
            retryable=True,
            status_code=response.status_code,
        )

    endpoints = payload.get("endpoints")

    if not isinstance(endpoints, list):
        raise DocuMintError(
            code="MISSING_ENDPOINTS",
            message=(
                "The generated response did not contain "
                "a valid endpoint list."
            ),
            retryable=True,
            status_code=response.status_code,
        )

    return payload


# ---------------------------------------------------------
# Session-state setup
# ---------------------------------------------------------

DEFAULT_STATE: dict[str, Any] = {
    "generated_result": None,
    "last_submitted_code": "",
    "error_code": None,
    "error_message": None,
    "error_request_id": None,
    "error_retryable": False,
    "error_status_code": None,
}

for key, default_value in DEFAULT_STATE.items():
    if key not in st.session_state:
        st.session_state[key] = default_value


def clear_error() -> None:
    """Clear the currently stored error."""

    st.session_state.error_code = None
    st.session_state.error_message = None
    st.session_state.error_request_id = None
    st.session_state.error_retryable = False
    st.session_state.error_status_code = None


def store_error(error: DocuMintError) -> None:
    """Store an application error for display."""

    st.session_state.generated_result = None
    st.session_state.error_code = error.code
    st.session_state.error_message = error.message
    st.session_state.error_request_id = error.request_id
    st.session_state.error_retryable = error.retryable
    st.session_state.error_status_code = error.status_code


def generate_documentation(raw_code: str) -> None:
    """Run one documentation-generation request."""

    clear_error()

    st.session_state.generated_result = None
    st.session_state.last_submitted_code = raw_code

    if not raw_code.strip():
        store_error(
            DocuMintError(
                code="EMPTY_INPUT",
                message=(
                    "Paste C#/.NET controller code before "
                    "generating documentation."
                ),
                retryable=False,
                status_code=400,
            )
        )
        return

    if len(raw_code) > MAX_INPUT_CHARACTERS:
        store_error(
            DocuMintError(
                code="INPUT_TOO_LARGE",
                message=(
                    f"The submitted code exceeds the "
                    f"{MAX_INPUT_CHARACTERS:,}-character limit."
                ),
                retryable=False,
                status_code=413,
            )
        )
        return

    started_at = time.perf_counter()

    try:
        with st.spinner(
            "Analyzing controller code and generating "
            "documentation..."
        ):
            result = call_backend(raw_code)

        total_duration_seconds = (
            time.perf_counter() - started_at
        )

        metadata = result.get("meta")

        if not isinstance(metadata, dict):
            metadata = {}
            result["meta"] = metadata

        metadata["clientDurationSeconds"] = (
            total_duration_seconds
        )

        st.session_state.generated_result = result

    except DocuMintError as exc:
        store_error(exc)

    except Exception as exc:
        store_error(
            DocuMintError(
                code="UNEXPECTED_FRONTEND_ERROR",
                message=(
                    "The interface encountered an unexpected "
                    "error."
                ),
                retryable=True,
            )
        )

        print(
            f"Unexpected Streamlit error: {exc}"
        )


# ---------------------------------------------------------
# Documentation renderer
# ---------------------------------------------------------

def render_documentation(
    data: dict[str, Any],
) -> None:
    """Render generated documentation using three columns."""

    endpoints = data.get("endpoints", [])

    if not endpoints:
        st.info(
            "No API endpoints were detected. Submit a "
            "controller containing explicit route attributes."
        )
        return

    st.header("📄 Generated API Documentation")

    st.warning(
        "Review before use: generated documentation may "
        "require developer verification."
    )

    # -----------------------------------------------------
    # Generation metrics
    # -----------------------------------------------------

    metadata = data.get("meta", {})

    if isinstance(metadata, dict):
        metric_columns = st.columns(
            3,
            gap="medium",
        )

        backend_duration_ms = metadata.get(
            "durationMilliseconds"
        )

        client_duration_seconds = metadata.get(
            "clientDurationSeconds"
        )

        model_name = str(
            metadata.get(
                "model",
                "Unknown",
            )
        )

        with metric_columns[0]:
            if isinstance(
                client_duration_seconds,
                (int, float),
            ):
                st.metric(
                    "Total Generation Time",
                    f"{client_duration_seconds:.2f}s",
                )
            else:
                st.metric(
                    "Total Generation Time",
                    "N/A",
                )

        with metric_columns[1]:
            if isinstance(
                backend_duration_ms,
                (int, float),
            ):
                st.metric(
                    "Backend Processing",
                    f"{backend_duration_ms / 1000:.2f}s",
                )
            else:
                st.metric(
                    "Backend Processing",
                    "N/A",
                )

        with metric_columns[2]:
            st.metric(
                "AI Model",
                shorten_model_name(model_name),
            )

    valid_endpoint_indexes = [
        index
        for index, endpoint in enumerate(endpoints)
        if isinstance(endpoint, dict)
    ]

    if not valid_endpoint_indexes:
        st.error(
            "The generated endpoint list contains no "
            "readable endpoint objects."
        )
        return

    navigation_column, details_column, example_column = (
       st.columns([2.0, 3.2, 2.3], gap="medium")
    )

    # -----------------------------------------------------
    # Column 1: Endpoint selector
    # -----------------------------------------------------

    with navigation_column:
        st.subheader("🧭 Endpoints")

        selected_index = st.radio(
            "Available endpoints",
            options=valid_endpoint_indexes,
            format_func=lambda index: endpoint_label(
                endpoints[index]
            ),
            label_visibility="collapsed",
        )

    selected_endpoint = endpoints[selected_index]

    method = str(
        selected_endpoint.get(
            "method",
            "UNKNOWN",
        )
    ).upper()

    path = normalize_path(
        str(
            selected_endpoint.get(
                "path",
                "/",
            )
        )
    )

    description = selected_endpoint.get(
        "description",
        "No description was generated.",
    )

    parameters = selected_endpoint.get(
        "parameters",
        [],
    )

    warnings = selected_endpoint.get(
        "warnings",
        [],
    )

    curl_example = selected_endpoint.get(
        "curl_example",
        "No curl example was generated.",
    )

    # -----------------------------------------------------
    # Column 2: Endpoint details
    # -----------------------------------------------------

    with details_column:
        st.subheader("🔍 Details & Parameters")

        st.markdown(
            f"### **{method}** `{path}`"
        )

        st.write(description)

        if isinstance(parameters, list) and parameters:
            display_parameters: list[dict[str, Any]] = []

            for parameter in parameters:
                if not isinstance(parameter, dict):
                    continue

                display_parameters.append(
                    {
                        "Name": parameter.get(
                            "name",
                            "",
                        ),
                        "Type": parameter.get(
                            "type",
                            "unknown",
                        ),
                        "Required": parameter.get(
                            "required",
                            False,
                        ),
                        "Description": parameter.get(
                            "description",
                            "",
                        ),
                    }
                )

            if display_parameters:
                st.markdown("#### Parameters")

                # No height="content" is used because older
                # Streamlit versions do not support it.
                st.dataframe(
                    display_parameters,
                    hide_index=True,
                    use_container_width=True,
                    column_order=[
                        "Name",
                        "Type",
                        "Required",
                        "Description",
                    ],
                    column_config={
                        "Name": st.column_config.TextColumn(
                            "Name",
                            width="medium",
                        ),
                        "Type": st.column_config.TextColumn(
                            "Type",
                            width="small",
                        ),
                        "Required": (
                            st.column_config.CheckboxColumn(
                                "Required",
                                width="small",
                            )
                        ),
                        "Description": (
                            st.column_config.TextColumn(
                                "Description",
                                width="large",
                            )
                        ),
                    },
                )

            else:
                st.caption(
                    "No readable parameters were generated."
                )

        else:
            st.caption("No parameters detected.")

        if isinstance(warnings, list) and warnings:
            st.markdown("#### Warnings")

            for warning in warnings:
                st.warning(str(warning))

    # -----------------------------------------------------
    # Column 3: curl request example
    # -----------------------------------------------------

    with example_column:
        st.subheader("💻 Request Example")

        st.markdown(
            f"**Request for `{path}`**"
        )

        st.caption(
            "Use the copy icon or scroll horizontally "
            "to view the complete request."
        )

        st.code(
            str(curl_example),
            language="bash",
        )


# ---------------------------------------------------------
# Page setup
# ---------------------------------------------------------

st.set_page_config(
    page_title="DocuMint AI",
    page_icon="🌱",
    layout="wide",
)

st.title("🌱 DocuMint AI")

st.write(
    "Generate a structured first draft of API "
    "documentation from C#/.NET backend controller code."
)

st.caption(
    "v1.0 MVP • Privacy-aware processing • "
    "No intentional application-side source-code storage"
)

st.info(
    "Use only sample, anonymized, open-source, or "
    "non-confidential code. Do not submit passwords, "
    "API keys, access tokens, or proprietary production code."
)


# ---------------------------------------------------------
# Input form
# ---------------------------------------------------------

with st.form(
    "documentation_generation_form",
    clear_on_submit=False,
    enter_to_submit=False,
):
    raw_code = st.text_area(
        "Source Code Input",
        value=st.session_state.last_submitted_code,
        placeholder=(
            'Paste a C#/.NET controller here...\n\n'
            '[HttpGet("/api/v1/users/{id}")]\n'
            "public IActionResult GetUser(int id)\n"
            "{\n"
            "    // ...\n"
            "}"
        ),
        height=300,
        max_chars=MAX_INPUT_CHARACTERS,
    )

    submitted = st.form_submit_button(
        "Generate Documentation ✨",
        type="primary",
        use_container_width=True,
    )


if submitted:
    generate_documentation(raw_code)


# ---------------------------------------------------------
# Error feedback and retry
# ---------------------------------------------------------

if st.session_state.error_message:
    st.error(
        f"❌ {st.session_state.error_message}"
    )

    with st.expander("Technical details"):
        st.code(
            st.session_state.error_code
            or "UNKNOWN_ERROR"
        )

        if st.session_state.error_status_code:
            st.caption(
                "HTTP status: "
                f"{st.session_state.error_status_code}"
            )

        if st.session_state.error_request_id:
            st.caption(
                "Request ID: "
                f"{st.session_state.error_request_id}"
            )

    if st.session_state.error_retryable:
        if st.button(
            "Retry generation",
            type="primary",
        ):
            generate_documentation(
                st.session_state.last_submitted_code
            )


# ---------------------------------------------------------
# Display generated documentation
# ---------------------------------------------------------

if isinstance(
    st.session_state.generated_result,
    dict,
):
    render_documentation(
        st.session_state.generated_result
    )