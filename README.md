# DocuMint AI

DocuMint AI is an AI-powered developer productivity tool that generates structured API documentation from C#/.NET controller code.

## Problem

API documentation often becomes outdated because updates depend on manual developer effort. Developers may spend time reading source code, debugging unclear parameters, or contacting API owners to understand endpoint behavior.

## Solution

DocuMint AI generates a first draft of API documentation from backend controller code. It extracts:

- HTTP methods
- Route paths
- Parameters
- Required vs optional fields
- Endpoint descriptions
- cURL examples

## Product Management Work

This project was built as a Product Management portfolio project.

Key PM artifacts included:

- User research with 10 developers
- PRD creation
- MVP scoping
- Jobs-to-be-done
- North Star Metric definition
- Success metrics
- Error-state testing
- Accuracy validation
- Beta testing with 5 developers in progress

## North Star Metric

Reduce the time developers spend understanding undocumented or unclear APIs.

## Validation Results

Controlled MVP testing showed:

- 8 controller scenarios tested
- 97 of 97 fields extracted correctly
- 100% field-level accuracy on controlled test cases
- 1.31 second median generation latency
- 7 error-state scenarios tested

Note: These results are based on controlled sample C#/.NET controllers and do not represent guaranteed production accuracy.

## Tech Stack

### Frontend

- Streamlit
- Python

### Backend

- ASP.NET Core / .NET
- Gemini API
- Structured JSON outputs

## Supported Input

MVP v1.0 officially supports:

- C#/.NET controller code

Python, TypeScript, and other languages are not officially supported in the MVP.

## Privacy & Safety

- Do not submit confidential or proprietary code.
- Do not submit passwords, API keys, access tokens, or secrets.
- The app includes basic secret-detection guardrails.
- AI-generated documentation should be reviewed by developers before use.

## How to Run Locally

### Backend

```bash
cd backend
export GEMINI_API_KEY="your-api-key"
dotnet run
dotnet run
