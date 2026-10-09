//! `POST /api/<command>` with the JSON arguments as body; replies with the JSON result, or 500 and the error text.
//! Vite proxies `/api` here (app/vite.config.ts), so `npm run dev` gives the full app in a browser.

use std::sync::Arc;

use serde_json::Value as Json;
use shadercalc_backend::Backend;
use tiny_http::{Header, Method, Request, Response, Server};

const ADDRESS: &str = "127.0.0.1:5191";

fn respond(request: Request, status: u16, body: String) {
    let header: Header = Header::from_bytes("Content-Type", "application/json; charset=utf-8").expect("valid header");
    let _ = request.respond(Response::from_string(body).with_status_code(status).with_header(header));
}

fn serve(backend: &Backend, mut request: Request) {
    let command: Option<String> = request.url().strip_prefix("/api/").map(str::to_string);
    let Some(command) = command.filter(|_| *request.method() == Method::Post) else {
        respond(request, 404, "\"not found\"".to_string());
        return;
    };
    let mut body: String = String::new();
    if let Err(error) = request.as_reader().read_to_string(&mut body) {
        respond(request, 400, Json::String(error.to_string()).to_string());
        return;
    }
    let arguments: Json =
        if body.trim().is_empty() { Json::Null } else { serde_json::from_str(&body).unwrap_or(Json::Null) };
    match backend.handle(&command, &arguments) {
        Ok(result) => respond(request, 200, result.to_string()),
        Err(error) => respond(request, 500, Json::String(error).to_string()),
    }
}

fn main() {
    let backend: Arc<Backend> =
        Arc::new(Backend::open().unwrap_or_else(|error| panic!("can't open the worksheets: {error}")));
    let server: Server = Server::http(ADDRESS).unwrap_or_else(|error| panic!("can't listen on {ADDRESS}: {error}"));
    println!("ShaderCalc dev server on http://{ADDRESS}");
    for request in server.incoming_requests() {
        let backend: Arc<Backend> = backend.clone();
        std::thread::spawn(move || serve(&backend, request));
    }
}
