// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     https://github.com/FastTunnel/FastTunnel/edit/v2/LICENSE
// Copyright (c) 2019 Gui.H

using FastTunnel.Api.Filters;
using FastTunnel.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FastTunnel.Api.Controllers
{
    [Authorize]
    [Route("api/[controller]/[action]")]
    [ApiController]
    [ServiceFilter(typeof(CustomExceptionFilterAttribute))]
    public class BaseController : ControllerBase
    {
        protected ApiResponse ApiResponse = new ApiResponse();
    }
}
